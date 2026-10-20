using System;
using System.Collections.Generic;

namespace PeerDesk.Server
{
    public sealed class PeerPresenceRow
    {
        public PeerPresenceRow(string deviceId,string state)
        {
            DeviceId=deviceId ?? throw new ArgumentNullException(nameof(deviceId));
            State=state ?? throw new ArgumentNullException(nameof(state));
        }
        public string DeviceId { get; }
        public string State { get; }
    }

    // Ephemeral, bounded presence observations and the approved/connected pair allow-list.
    public sealed class PeerPresenceState
    {
        private const int PairLimit=5000, SeenLimit=1000;
        private static readonly TimeSpan OnlineWindow=TimeSpan.FromSeconds(30);
        private readonly object gate=new object();
        private readonly int maxPairs, maxSeen;
        private readonly Dictionary<Pair,LinkedListNode<Pair>> pairs=new Dictionary<Pair,LinkedListNode<Pair>>();
        private readonly LinkedList<Pair> pairFifo=new LinkedList<Pair>();
        private readonly Dictionary<string,Seen> lastSeen=new Dictionary<string,Seen>(StringComparer.Ordinal);
        private readonly LinkedList<string> seenFifo=new LinkedList<string>();

        private sealed class Seen
        {
            internal DateTimeOffset At;
            internal LinkedListNode<string>? Node;
        }
        private struct Pair : IEquatable<Pair>
        {
            internal readonly string First,Second;
            internal Pair(string a,string b)
            {
                if(StringComparer.Ordinal.Compare(a,b)<=0) { First=a; Second=b; }
                else { First=b; Second=a; }
            }
            public bool Equals(Pair other) => String.Equals(First,other.First,StringComparison.Ordinal)&&String.Equals(Second,other.Second,StringComparison.Ordinal);
            public override bool Equals(object? obj) => obj is Pair&&Equals((Pair)obj);
            public override int GetHashCode() => unchecked((StringComparer.Ordinal.GetHashCode(First)*397)^StringComparer.Ordinal.GetHashCode(Second));
        }

        public PeerPresenceState(int maxPairs=PairLimit,int maxSeen=SeenLimit)
        {
            if(maxPairs<=0) throw new ArgumentOutOfRangeException(nameof(maxPairs));
            if(maxSeen<=0) throw new ArgumentOutOfRangeException(nameof(maxSeen));
            this.maxPairs=Math.Min(PairLimit,maxPairs);
            this.maxSeen=Math.Min(SeenLimit,maxSeen);
        }

        public void RememberApprovedPair(string a,string b)
        {
            ValidateId(a,nameof(a)); ValidateId(b,nameof(b));
            if(String.Equals(a,b,StringComparison.Ordinal)) throw new ArgumentException("A device cannot be paired with itself.");
            var pair=new Pair(a,b);
            lock(gate)
            {
                if(pairs.ContainsKey(pair)) return;
                var node=pairFifo.AddLast(pair); pairs.Add(pair,node);
                if(pairs.Count>maxPairs)
                {
                    Pair oldest=pairFifo.First!.Value; pairFifo.RemoveFirst(); pairs.Remove(oldest);
                }
            }
        }

        public void MarkSelfSeen(string id,DateTimeOffset now)
        {
            ValidateId(id,nameof(id));
            lock(gate)
            {
                Seen? seen;
                if(lastSeen.TryGetValue(id,out seen))
                {
                    seen.At=now; seenFifo.Remove(seen.Node!); seen.Node=seenFifo.AddLast(id);
                    return;
                }
                var node=seenFifo.AddLast(id); lastSeen.Add(id,new Seen { At=now,Node=node });
                if(lastSeen.Count>maxSeen)
                {
                    string oldest=seenFifo.First!.Value; seenFifo.RemoveFirst(); lastSeen.Remove(oldest);
                }
            }
        }

        public IReadOnlyList<PeerPresenceRow> Lookup(string self,IReadOnlyList<string> ids,DateTimeOffset now,Func<string,bool> deviceExists)
        {
            ValidateId(self,nameof(self));
            if(ids==null) throw new ArgumentNullException(nameof(ids));
            if(deviceExists==null) throw new ArgumentNullException(nameof(deviceExists));
            var rows=new List<PeerPresenceRow>(ids.Count);
            var unique=new HashSet<string>(StringComparer.Ordinal);
            lock(gate)
            {
                bool selfExists=deviceExists(self);
                for(int i=0;i<ids.Count;i++)
                {
                    string id=ids[i];
                    if(String.IsNullOrEmpty(id)||!unique.Add(id)) continue;
                    string state="unknown";
                    if(selfExists&&!String.Equals(self,id,StringComparison.Ordinal)&&pairs.ContainsKey(new Pair(self,id))&&deviceExists(id))
                    {
                        Seen? seen;
                        if(lastSeen.TryGetValue(id,out seen))
                        {
                            TimeSpan age=now-seen.At;
                            if(age>=TimeSpan.Zero) state=age<OnlineWindow?"online":"offline";
                        }
                        else state="offline";
                    }
                    rows.Add(new PeerPresenceRow(id,state));
                }
            }
            return rows.AsReadOnly();
        }

        private static void ValidateId(string id,string name)
        {
            if(String.IsNullOrWhiteSpace(id)) throw new ArgumentException("Device ID is required.",name);
        }
    }
}
