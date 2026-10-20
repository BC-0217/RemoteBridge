using PeerDesk.Server;

static void Check(bool value,string message) { if(!value) throw new InvalidOperationException(message); }
static string State(IReadOnlyList<PeerPresenceRow> rows,string id) => rows.Single(r=>r.DeviceId==id).State;
var now=DateTimeOffset.Parse("2026-10-17T12:00:00Z");
var state=new PeerPresenceState();
state.RememberApprovedPair("1111111111","2222222222");
state.MarkSelfSeen("1111111111",now);
state.MarkSelfSeen("2222222222",now.AddSeconds(-29));
var rows=state.Lookup("1111111111",new[]{"2222222222","3333333333"},now,id=>id!="3333333333");
Check(State(rows,"2222222222")=="online","recent allowed pair should be online");
Check(State(rows,"3333333333")=="unknown","unapproved/unregistered must be unknown");
Check(state.Lookup("1111111111",new[]{"2222222222"},now,id=>true) is not List<PeerPresenceRow>,"lookup must be read-only");

state.MarkSelfSeen("2222222222",now.AddSeconds(-30));
Check(State(state.Lookup("1111111111",new[]{"2222222222"},now,id=>true),"2222222222")=="offline","30-second boundary must be offline");
Check(State(state.Lookup("1111111111",new[]{"2222222222"},now.AddSeconds(-31),id=>true),"2222222222")=="unknown","negative age after clock rollback must be unknown");
Check(State(state.Lookup("1111111111",new[]{"4444444444"},now,id=>true),"4444444444")=="unknown","unapproved pair must be unknown");
state.RememberApprovedPair("1111111111","4444444444");
Check(State(state.Lookup("1111111111",new[]{"4444444444"},now,id=>true),"4444444444")=="offline","allowed unseen peer should be offline");
Check(State(state.Lookup("1111111111",new[]{"2222222222"},now,id=>id!="2222222222"),"2222222222")=="unknown","revoked/unregistered peer must be unknown");

var bounded=new PeerPresenceState(maxPairs:1);
bounded.RememberApprovedPair("a","b"); bounded.RememberApprovedPair("b","a");
bounded.RememberApprovedPair("b","c");
Check(State(bounded.Lookup("a",new[]{"b"},now,id=>true),"b")=="unknown","oldest approved pair was not evicted");
Check(State(bounded.Lookup("b",new[]{"c"},now,id=>true),"c")=="offline","newest pair should remain approved");

var seenBounded=new PeerPresenceState(maxSeen:1);
seenBounded.RememberApprovedPair("x","y"); seenBounded.MarkSelfSeen("y",now); seenBounded.MarkSelfSeen("z",now);
Check(State(seenBounded.Lookup("x",new[]{"y"},now,id=>true),"y")=="offline","seen cache must remain bounded and fall back to offline");

var restarted=new PeerPresenceState();
Check(State(restarted.Lookup("1111111111",new[]{"2222222222"},now,id=>true),"2222222222")=="unknown","new state instance must not restore state");
Console.WriteLine("PASS: approved-pair gate, online/offline/unknown, 30s boundary, clock rollback, revocation, bounded FIFO and restart semantics.");
