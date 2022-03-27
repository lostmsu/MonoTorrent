//
// AnnounceRequest.cs
//
// Authors:
//   Gregor Burger burger.gregor@gmail.com
//
// Copyright (C) 2006 Gregor Burger
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
using System.Collections.Specialized;
using System.Net;
using System.Web;

using MonoTorrent.BEncoding;

namespace MonoTorrent.TrackerServer
{
    public class AnnounceRequest : TrackerRequest
    {
        static readonly string[] MandatoryFields = {
            "info_hash", "peer_id", "port", "uploaded", "downloaded", "left", "compact"
        };

        // FIXME: Expose these as configurable options
        internal static readonly int DefaultWanted = 30;
        internal static readonly bool UseTrackerKey = false;

        /// <summary>
        /// This is the IPAddress/Port that the client is listening for incoming connections on. If the
        /// announce request contained an 'ip' parameter then it is used, otherwise the actual IP from the
        /// request is used.
        /// </summary>
        public IPEndPoint ClientAddress { get; }

        /// <summary>
        /// The total number of bytes downloaded since the 'Started' event was sent.
        /// </summary>
        public int Downloaded => ParseInt ("downloaded");

        /// <summary>
        /// The event, if any, associated with this announce
        /// </summary>
        public TorrentEvent Event {
            get {
                if (Parameters["event"] is string e) {
                    if (e.Equals ("started"))
                        return TorrentEvent.Started;
                    if (e.Equals ("stopped"))
                        return TorrentEvent.Stopped;
                    if (e.Equals ("completed"))
                        return TorrentEvent.Completed;
                }

                return TorrentEvent.None;
            }
        }

        /// <summary>
        /// The number of bytes which still need to be downloaded to make the torrent 100% complete.
        /// </summary>
        public int Left => ParseInt ("left");

        /// <summary>
        /// True if the peers should be returned in compact form.
        /// </summary>
        public bool HasRequestedCompact => ParseInt ("compact") == 1;

        /// <summary>
        /// The infohash of the torrent this request is associated with.
        /// </summary>
        public InfoHash? InfoHash { get; }

        /// <summary>
        /// An arbitrary identifier generated by the client which can be used to track the client even if
        /// it's IP address changes. This is never shared with other peers.
        /// </summary>
        public BEncodedString? Key => Parameters["key"] is String str ? BEncodedString.UrlDecode (str) : null;

        /// <summary>
        /// Returns false if any required parameters are missing from the request. If this occurs the 'Response'
        /// dictionary will be populated with the appropriate error message.
        /// </summary>
        public override bool IsValid => InfoHash != null;

        /// <summary>
        /// The number of peers the client wants to receive. If unspecified then the tracker default amount
        /// be returned.
        /// </summary>
        public int NumberWanted => ParseInt ("numwant", DefaultWanted);

        /// <summary>
        /// The 20 byte identifier for the peer. This is shared with other peers when a non-compact response
        /// is returned.
        /// </summary>
        public BEncodedString? PeerId => Parameters["peer_id"] is string value ? BEncodedString.UrlDecode (value) : null;

        /// <summary>
        /// The port the client is listening for incoming connections on.
        /// </summary>
        public int Port => ParseInt ("port");

        /// <summary>
        /// The first time a peer announces to a tracker, we send it the TrackerId
        /// of this tracker. Subsequent announce requests should send that value.
        /// </summary>
        public BEncodedString? TrackerId => Parameters["trackerid"] is string str ? BEncodedString.UrlDecode (str) : null;

        /// <summary>
        /// The total amount of bytes uploaded since the 'Started' event was sent.
        /// </summary>
        public long Uploaded => ParseInt ("uploaded");

        public AnnounceRequest (NameValueCollection collection, IPAddress address)
            : base (collection, address)
        {
            InfoHash = CheckMandatoryFields ();

            /* If the user has supplied an IP address, we use that instead of
             * the IP address we read from the announce request connection. */
            if (IPAddress.TryParse (Parameters["ip"] ?? "", out IPAddress? supplied) && !supplied.Equals (IPAddress.Any))
                ClientAddress = new IPEndPoint (supplied, Port);
            else
                ClientAddress = new IPEndPoint (address, Port);
        }

        InfoHash? CheckMandatoryFields ()
        {
            var keys = new List<string?> (Parameters.AllKeys);
            foreach (string field in MandatoryFields) {
                if (keys.Contains (field))
                    continue;

                Response.Add (FailureKey, new BEncodedString ("mandatory announce parameter " + field + " in query missing"));
                return null;
            }
            var hash = HttpUtility.UrlDecodeToBytes (Parameters["info_hash"]);
            if (hash is null || hash.Length != 20) {
                Response.Add (FailureKey, new BEncodedString (
                    $"infohash was {hash?.Length ?? 0} bytes long, it must be 20 bytes long."));
                return null;
            }
            return InfoHash.FromMemory (hash);
        }

        int ParseInt (string str, int? defaultValue = null)
        {
            if (Parameters[str] is string value && int.TryParse (value, out int result))
                return result;
            return defaultValue ?? 0;
        }
    }
}
