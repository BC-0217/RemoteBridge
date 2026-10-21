using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using System.Drawing;

namespace PeerDesk.Windows
{
    internal sealed partial class MainForm
    {
        private readonly System.Windows.Forms.Timer presenceTimer = new System.Windows.Forms.Timer { Interval = 30000 };
        private CancellationTokenSource presenceCancellation;
        private readonly Dictionary<string,string> presenceStates = new Dictionary<string,string>(StringComparer.Ordinal);
        private bool presenceBusy;
        private bool presenceInitialized;
        private ControlPlaneClient presenceStateClient;
        private string presenceStateUrl,presenceStatePin,presenceStateDeviceId;
        private long presenceStateRevision,presenceStateEpoch;

        // Call once after the form is initialized; call StopPresence() during shutdown.
        private void InitializePresence()
        {
            if(presenceInitialized) return;
            presenceInitialized=true;
            presenceCancellation=new CancellationTokenSource();
            historyList.DrawMode=DrawMode.OwnerDrawFixed;
            historyList.DrawItem+=DrawPresenceHistoryItem;
            presenceTimer.Tick+=async (s,e)=>await RefreshPresenceAsync();
            presenceTimer.Start();
            _=RefreshPresenceAsync();
        }

        private void StopPresence()
        {
            if(!presenceInitialized) return;
            presenceInitialized=false;
            presenceTimer.Stop();
            presenceTimer.Dispose();
            if(presenceCancellation!=null) { presenceCancellation.Cancel(); presenceCancellation.Dispose(); presenceCancellation=null; }
            presenceBusy=false;
            presenceStates.Clear();presenceStateClient=null;presenceStateUrl=null;presenceStatePin=null;presenceStateDeviceId=null;
            if(historyList!=null&&!historyList.IsDisposed)
            {
                historyList.DrawItem-=DrawPresenceHistoryItem;
                historyList.DrawMode=DrawMode.Normal;
                historyList.Invalidate();
            }
        }

        private async Task RefreshPresenceAsync()
        {
            if(!presenceInitialized||presenceBusy||shutdownBusy||shutdownComplete||IsDisposed||!registrationVerified||client==null) return;
            var c=client;
            var token=c.Token;
            var deviceId=c.DeviceId;
            var revision=c.CredentialRevision;
            var epoch=autoEpoch;
            ServerProfile profile;
            try { profile=ReadProfile(); }
            catch { presenceStates.Clear();historyList.Invalidate();return; }
            string serverUrl=c.ServerUrl, pin=c.CertificateSha256;
            if(String.IsNullOrEmpty(token)||String.IsNullOrEmpty(deviceId)||profile==null) return;
            var records=history.ForServer(serverUrl,pin);
            var ids=records.Select(x=>x.TargetDeviceId).Where(IsPresenceDeviceId).Distinct(StringComparer.Ordinal).Take(20).ToArray();
            if(ids.Length==0) { presenceStates.Clear();historyList.Invalidate();return; }
            presenceBusy=true;
            var cancellation=presenceCancellation;
            if(cancellation==null) {presenceBusy=false;return;}
            var ct=cancellation.Token;
            try
            {
                string path="/api/v2/peers/presence?ids="+String.Join(",",ids);
                string raw=await c.SendJsonAsync("GET",path,null,ct).ConfigureAwait(true);
                if(!PresenceBindingCurrent(c,deviceId,token,revision,serverUrl,pin,profile,epoch)) return;
                var serializer=new JavaScriptSerializer { MaxJsonLength=8192 };
                var root=serializer.Deserialize<Dictionary<string,object>>(raw);
                object peersValue;
                var next=new Dictionary<string,string>(StringComparer.Ordinal);
                if(root==null||root.Count!=1||!root.TryGetValue("peers",out peersValue)||!(peersValue is IList)||((IList)peersValue).Count>20)
                    throw new InvalidOperationException("presence response schema");
                foreach(object item in (IList)peersValue)
                {
                    var row=item as Dictionary<string,object>;
                    object idValue,stateValue;
                    if(row==null||row.Count!=2||!row.TryGetValue("deviceId",out idValue)||!row.TryGetValue("state",out stateValue))
                        throw new InvalidOperationException("presence row schema");
                    string id=idValue as string,state=stateValue as string;
                    if(!IsPresenceDeviceId(id)||!ids.Contains(id,StringComparer.Ordinal)||
                       (state!="online"&&state!="offline"&&state!="unknown")||next.ContainsKey(id))
                        throw new InvalidOperationException("presence row value");
                    next[id]=state;
                }
                foreach(string id in ids) if(!next.ContainsKey(id)) next[id]="unknown";
                if(!PresenceBindingCurrent(c,deviceId,token,revision,serverUrl,pin,profile,epoch)) return;
                presenceStates.Clear();
                foreach(var pair in next) presenceStates[pair.Key]=pair.Value;
                presenceStateClient=c;presenceStateUrl=serverUrl;presenceStatePin=pin;presenceStateDeviceId=deviceId;
                presenceStateRevision=revision;presenceStateEpoch=epoch;
                historyList.Invalidate();
            }
            catch(OperationCanceledException) { }
            catch
            {
                if(PresenceBindingCurrent(c,deviceId,token,revision,serverUrl,pin,profile,epoch))
                {
                    presenceStates.Clear();
                    foreach(string id in ids) presenceStates[id]="unknown";
                    presenceStateClient=c;presenceStateUrl=serverUrl;presenceStatePin=pin;presenceStateDeviceId=deviceId;
                    presenceStateRevision=revision;presenceStateEpoch=epoch;
                    if(!historyList.IsDisposed) historyList.Invalidate();
                }
            }
            finally { presenceBusy=false; }
        }

        private bool PresenceBindingCurrent(ControlPlaneClient c,string deviceId,string token,long revision,string url,string pin,ServerProfile profile,long epoch)
        {
            if(!presenceInitialized||shutdownBusy||shutdownComplete||IsDisposed||!registrationVerified||
               !ReferenceEquals(client,c)||c.CredentialRevision!=revision||c.DeviceId!=deviceId||c.Token!=token||
               c.ServerUrl!=url||c.CertificateSha256!=pin||autoEpoch!=epoch) return false;
            try
            {
                ServerProfile now=ReadProfile();
                return now!=null&&String.Equals(now.RendezvousUrl,profile.RendezvousUrl,StringComparison.Ordinal)&&
                    String.Equals(now.CertificateSha256,profile.CertificateSha256,StringComparison.Ordinal);
            }
            catch { return false; }
        }

        private bool PresenceDisplayBindingCurrent()
        {
            if(presenceStateClient==null||!presenceInitialized||shutdownBusy||shutdownComplete||IsDisposed||!registrationVerified||
               !ReferenceEquals(client,presenceStateClient)||client.CredentialRevision!=presenceStateRevision||
               client.DeviceId!=presenceStateDeviceId||client.ServerUrl!=presenceStateUrl||client.CertificateSha256!=presenceStatePin||autoEpoch!=presenceStateEpoch)
                return false;
            try
            {
                ServerProfile current=ReadProfile();
                return current!=null&&String.Equals(current.RendezvousUrl,presenceStateUrl,StringComparison.Ordinal)&&
                    String.Equals(current.CertificateSha256,presenceStatePin,StringComparison.Ordinal);
            }
            catch { return false; }
        }

        private static bool IsPresenceDeviceId(string id)
        {
            return id!=null&&Regex.IsMatch(id,@"^[0-9]{10}$",RegexOptions.CultureInvariant);
        }

        private void DrawPresenceHistoryItem(object sender,DrawItemEventArgs e)
        {
            e.DrawBackground();
            if(e.Index>=0&&e.Index<historyList.Items.Count)
            {
                var record=historyList.Items[e.Index] as ConnectionHistoryRecord;
                string label=record==null?Convert.ToString(historyList.Items[e.Index]):record.ToString();
                if(record!=null)
                {
                    string state;
                    if(PresenceDisplayBindingCurrent()&&presenceStates.TryGetValue(record.TargetDeviceId,out state))
                        label+="    · "+(state=="online"?UiLocalization.Text("온라인"):state=="offline"?UiLocalization.Text("오프라인"):UiLocalization.Text("확인 불가"));
                    else label+=UiLocalization.Text("    · 확인 불가");
                }
                TextRenderer.DrawText(e.Graphics,label,e.Font,e.Bounds,e.ForeColor,TextFormatFlags.Left|TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis);
            }
            e.DrawFocusRectangle();
        }
    }
}