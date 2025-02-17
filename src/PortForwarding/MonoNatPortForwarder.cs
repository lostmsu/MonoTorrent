﻿//
// MonoNatPortForwarder.cs
//
// Authors:
//   Alan McGovern <alan.mcgovern@gmail.com>
//
// Copyright (C) 2020 Alan McGovern
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//


using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Mono.Nat;

using MonoTorrent.Logging;

namespace MonoTorrent.PortForwarding
{
    public class MonoNatPortForwarder : IPortForwarder, IDisposable
    {
        readonly ILogger logger = LoggerFactory.Create (nameof (MonoNatPortForwarder));
        CancellationTokenSource stop = new CancellationTokenSource ();
        readonly List<ActiveMapping> active = new ();

        public event EventHandler? MappingsChanged;

        public bool Active => NatUtility.IsSearching;

        IReadOnlyList<INatDevice> Devices { get; set; }

        SemaphoreSlim Locker { get; } = new SemaphoreSlim (1);

        public Mappings Mappings { get; private set; }

        public MonoNatPortForwarder ()
        {
            Devices = new List<INatDevice> ();
            Mappings = Mappings.Empty;

            NatUtility.DeviceFound += this.OnDeviceFound;
        }

        async void OnDeviceFound(object? _, DeviceEventArgs e)
        {
            using (await Locker.EnterAsync ()) {
                if (Devices.Contains (e.Device))
                    return;
                Devices = Devices.Concat (new[] { e.Device }).ToArray ();

                foreach (var mapping in Mappings.Pending)
                    await CreateOrFailMapping (e.Device, mapping);
            }

            RaiseMappingsChangedAsync ();
        }

        public async Task RegisterMappingAsync (Mapping mapping)
        {
            using (await Locker.EnterAsync ()) {
                Mappings = Mappings.WithPending (mapping);
                if (!Active)
                    return;

                foreach (var device in Devices)
                    await CreateOrFailMapping (device, mapping);
            }
            RaiseMappingsChangedAsync ();
        }

        public async Task UnregisterMappingAsync (Mapping mapping, CancellationToken token)
        {
            using (await Locker.EnterAsync ()) {
                Mappings = Mappings.Remove (mapping, out bool wasCreated);
                if (!Active)
                    return;

                if (wasCreated) {
                    foreach (var device in Devices) {
                        token.ThrowIfCancellationRequested ();
                        await DeletePortMapping (device, mapping);
                    }
                }
            }
            RaiseMappingsChangedAsync ();
        }

        public async Task StartAsync (CancellationToken token)
        {
            using (await Locker.EnterAsync ()) {
                stop = new CancellationTokenSource ();
                if (!Active) {
                    await new ThreadSwitcher ();
                    NatUtility.StartDiscovery (NatProtocol.Pmp, NatProtocol.Upnp);
                }
                Tick (stop.Token);
            }
        }

        public Task StopAsync (CancellationToken token)
            => StopAsync (true, token);

        public async Task StopAsync (bool removeExisting, CancellationToken token)
        {
            using (await Locker.EnterAsync ()) {
                // cancel asynchronously
                stop.CancelAfter (0);

                NatUtility.StopDiscovery ();

                var created = Mappings.Created;
                Mappings = Mappings.WithAllPending ();
                try {
                    if (removeExisting) {
                        foreach (var mapping in created) {
                            foreach (var device in Devices) {
                                token.ThrowIfCancellationRequested ();
                                await DeletePortMapping (device, mapping);
                            }
                        }
                    }
                } finally {
                    Devices = new List<INatDevice> ();
                    RaiseMappingsChangedAsync ();
                }
            }
        }

        async Task CreateOrFailMapping (INatDevice device, Mapping mapping)
        {
            var map = new Mono.Nat.Mapping (
                mapping.Protocol == Protocol.Tcp ? Mono.Nat.Protocol.Tcp : Mono.Nat.Protocol.Udp,
                mapping.PrivatePort,
                mapping.PublicPort,
                0,
                $"int. {mapping.PrivatePort} -> ext. {mapping.PublicPort} {mapping.Protocol}"
            );

            try {
                await device.CreatePortMapAsync (map);
                Mappings = Mappings.WithCreated (mapping);
                lock (active)
                    active.Add (new (mapping, device, map));
                this.logger.Info ($"{Display(device)} successfully created mapping: {mapping} {map}");
            } catch (Exception e) {
                this.logger.Error ($"{Display (device)} failed to create mapping: {mapping}\n{e.Message}");
                Mappings = Mappings.WithFailed (mapping);
            }
        }

        async Task DeletePortMapping (INatDevice device, Mapping mapping)
        {
            var map = new Mono.Nat.Mapping (
                mapping.Protocol == Protocol.Tcp ? Mono.Nat.Protocol.Tcp : Mono.Nat.Protocol.Udp,
                mapping.PrivatePort,
                mapping.PublicPort,
                0,
                $"int. {mapping.PrivatePort} -> ext. {mapping.PublicPort} {mapping.Protocol}"
            );

            try {
                await device.DeletePortMapAsync (map).ConfigureAwait (false);
                lock (active)
                    active.Remove (new(mapping, device, map));
                this.logger.Info ($"{Display(device)} successfully deleted mapping: {mapping}");
            } catch (MappingException e) {
                string message = $"{Display (device)} failed to delete mapping: {mapping}\n{e.Message}";
                if (e.ErrorCode != NatErrors.NoSuchEntryInArray)
                    this.logger.Error (message);
                else
                    this.logger.Debug (message);
            } catch (Exception e) {
                this.logger.Error ($"{Display(device)} failed to delete mapping: {mapping}\n{e.Message}");
            }
        }

        async void Tick (CancellationToken token)
        {
            await new ThreadSwitcher ();

            uint iteration = 0;
            while (!token.IsCancellationRequested) {
                iteration++;

                ActiveMapping[] activeSnapshot;
                lock (active)
                    activeSnapshot = active.ToArray ();

                var replace = new List<(Mono.Nat.Mapping, ActiveMapping?)> ();

                foreach (var mapping in activeSnapshot) {
                    if (token.IsCancellationRequested)
                        return;

                    var map = mapping.NativeMapping;
                    var device = mapping.Device;
                    if (map.Lifetime == 0) {
                        try {
                            var existing = await device.GetSpecificMappingAsync (map.Protocol, map.PublicPort).ConfigureAwait (false);
                            this.logger.Debug ($"OK: {Display (device)} mapping {map.Protocol}({map.PublicPort})");
                            continue;
                        } catch (MappingException e) {
                            this.logger.Debug ($"{Display (device)} mapping {map.Protocol}({map.PublicPort}) not found: {e.Message}");
                        }
                    } else {
                        double timeLeft = (map.Expiration - DateTime.Now).TotalSeconds / (float) map.Lifetime;
                        if (timeLeft > 0.666)
                            continue;
                    }

                    this.logger.Debug ($"{Display (device)} refreshing mapping {map.Protocol}({map.PublicPort})");
                    try {
                        await device.DeletePortMapAsync (map).ConfigureAwait (false);
                    } catch (MappingException e) {
                        if (e.ErrorCode != NatErrors.NoSuchEntryInArray || map.Lifetime > 0)
                            this.logger.Error ($"{Display (device)} failed to delete mapping {map.Protocol}({map.PublicPort}): {e.Message}");
                    }
                    var newMap = new Mono.Nat.Mapping (
                            map.Protocol,
                            privatePort: map.PrivatePort,
                            publicPort: map.PublicPort);
                    try {
                        await device.CreatePortMapAsync (newMap).ConfigureAwait (false);
                        replace.Add ((map, mapping.WithNativeMapping (newMap)));
                        this.logger.Debug ($"{Display (device)} refreshed mapping {map.Protocol}({map.PublicPort})");
                        using (await Locker.EnterAsync ().ConfigureAwait (false)) {
                            Mappings = Mappings.WithCreated (mapping.Mapping);
                        }
                    } catch (MappingException e) {
                        this.logger.Error ($"{Display (device)} failed to recreate mapping {map.Protocol}({map.PublicPort}): {e.Message}");
                        using (await Locker.EnterAsync ().ConfigureAwait (false)) {
                            Mappings = Mappings.WithFailed (mapping.Mapping);
                        }
                        replace.Add ((map, null));
                    }
                }

                lock (active) {
                    int i = 0;
                    while (i < active.Count && replace.Count > 0) {
                        var current = active[i];
                        var (map, newMap) = replace.FirstOrDefault (r => r.Item1.Equals (current.NativeMapping));
                        if (map is null) {
                            i++;
                            continue;
                        }
                        if (newMap is null)
                            active.RemoveAt (i);
                        else
                            active[i++] = newMap;
                    }
                }

                if (replace.Any (replace => replace.Item2 is null))
                    RaiseMappingsChangedAsync ();

                RetryFailed (iteration: iteration);

                try {
                    await Task.Delay (TimeSpan.FromMinutes (1), token).ConfigureAwait (false);
                } catch (TaskCanceledException) {
                    continue;
                }
            }
        }

        async void RetryFailed (uint iteration)
        {
            var failed = Mappings.Failed;
            if (failed.Count == 0)
                return;
            var retry = Mappings.Failed[(int)(iteration % failed.Count)];
            await RegisterMappingAsync (retry);
        }

        class ActiveMapping: IEquatable<ActiveMapping>
        {
            public ActiveMapping (Mapping mapping, INatDevice device, Mono.Nat.Mapping nativeMapping)
            {
                Mapping = mapping ?? throw new ArgumentNullException (nameof (mapping));
                Device = device ?? throw new ArgumentNullException (nameof (device));
                NativeMapping = nativeMapping ?? throw new ArgumentNullException (nameof (nativeMapping));
            }

            public INatDevice Device { get; }
            public Mono.Nat.Mapping NativeMapping { get; }
            public Mapping Mapping { get; }

            public ActiveMapping WithNativeMapping (Mono.Nat.Mapping mapping)
                => new ActiveMapping (Mapping, Device, mapping);

            public bool Equals (ActiveMapping? other)
                => Device.Equals (other?.Device) && Mapping.Equals (other?.Mapping);

            public override bool Equals (object? obj)
                => obj is ActiveMapping other && Equals (other);

            public override int GetHashCode () => Device.GetHashCode () ^ Mapping.GetHashCode ();
        }

        static class NatErrors
        {
            public const ErrorCode NoSuchEntryInArray = (ErrorCode)714;
        }

        static string Display(INatDevice device) => $"{Display(device.NatProtocol)}({device.DeviceEndpoint})";
        static string Display(NatProtocol protocol) => protocol == NatProtocol.Pmp ? "PMP" : "UPnP";

        async void RaiseMappingsChangedAsync ()
        {
            if (MappingsChanged != null) {
                await new ThreadSwitcher ();
                MappingsChanged.Invoke (this, EventArgs.Empty);
            }
        }

        public void Dispose()
        {
            NatUtility.DeviceFound -= this.OnDeviceFound;
        }
    }
}
