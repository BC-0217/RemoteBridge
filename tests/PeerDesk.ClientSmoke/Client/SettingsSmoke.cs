using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Web.Script.Serialization;
using PeerDesk.Windows;
internal static class SettingsSmoke {
 public static void Run(Action<bool,string> check,string profileJson,ControlPlaneClient client) {
  string dir=Path.Combine(Path.GetTempPath(),"PeerDesk-settings-fixture-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
  string path=Path.Combine(dir,"settings.json");
  var state=new SettingsState{ProfileJson=profileJson,IsRegistered=true,DeviceId=client.DeviceId,BearerToken=client.Token,TemporaryPassword=client.TemporaryPassword,PasswordExpiresAt=client.PasswordExpiresAt,BoundServerUrl=client.ServerUrl,BoundCertificateSha256=client.CertificateSha256};
  var store=new SettingsStore(path);store.Save(state);
  string wire=File.ReadAllText(path);var loaded=store.Load();
  check(loaded.IsRegistered&&loaded.DeviceId==state.DeviceId&&loaded.BearerToken==state.BearerToken&&loaded.TemporaryPassword==state.TemporaryPassword,"DPAPI CurrentUser credential roundtrip");
  check(!wire.Contains(client.Token)&&!wire.Contains(client.TemporaryPassword)&&!wire.Contains("invitationCode"),"settings no plaintext credentials or invitation");
  var json=new JavaScriptSerializer();var outer=json.Deserialize<Dictionary<string,object>>(wire);
  check(outer.Count==4&&outer.ContainsKey("Version")&&(int)outer["Version"]==2&&outer.ContainsKey("ProfileJson")&&outer.ContainsKey("ProtectedCredentials")&&outer.ContainsKey("ProtectedInputs")&&outer["ProtectedInputs"]==null&&loaded.SavedInputs.Count==0&&new FileInfo(path).Length<=65536,"settings bounded public envelope");
  var changed=json.Deserialize<Dictionary<string,object>>(wire);
  changed["ProfileJson"]=profileJson.Replace(client.CertificateSha256,new string('1',64));
  string cross=Path.Combine(dir,"cross.json");File.WriteAllText(cross,json.Serialize(changed));
  bool denied=false;try{new SettingsStore(cross).Load();}catch(CryptographicException){denied=true;}catch(InvalidDataException){denied=true;}
  check(denied,"settings reject cross-server pin ciphertext transplant");
  changed=json.Deserialize<Dictionary<string,object>>(wire);changed["ProfileJson"]=profileJson.Replace(client.ServerUrl,"https://localhost:65530/");
  string crossUrl=Path.Combine(dir,"cross-url.json");File.WriteAllText(crossUrl,json.Serialize(changed));denied=false;
  try{new SettingsStore(crossUrl).Load();}catch(CryptographicException){denied=true;}catch(InvalidDataException){denied=true;}
  check(denied,"settings reject cross-server URL ciphertext transplant");
  changed=json.Deserialize<Dictionary<string,object>>(wire);byte[] ciphertext=Convert.FromBase64String((string)changed["ProtectedCredentials"]);ciphertext[ciphertext.Length-1]^=1;changed["ProtectedCredentials"]=Convert.ToBase64String(ciphertext);
  string tamper=Path.Combine(dir,"tamper.json");File.WriteAllText(tamper,json.Serialize(changed));denied=false;
  try{new SettingsStore(tamper).Load();}catch(CryptographicException){denied=true;}catch(InvalidDataException){denied=true;}
  check(denied,"settings reject modified DPAPI ciphertext");
  string huge=Path.Combine(dir,"huge.json");File.WriteAllBytes(huge,new byte[65537]);denied=false;
  try{new SettingsStore(huge).Load();}catch(InvalidDataException){denied=true;}
  check(denied&&new FileInfo(huge).Length==65537,"settings oversize bounded rejection preserves file");
  string bad=Path.Combine(dir,"bad.json");File.WriteAllText(bad,"not JSON");denied=false;
  try{new SettingsStore(bad).Load();}catch(ArgumentException){denied=true;}catch(InvalidDataException){denied=true;}catch(InvalidOperationException){denied=true;}
  check(denied&&File.ReadAllText(bad)=="not JSON","settings malformed preserves file");
  string duplicate=Path.Combine(dir,"duplicate.json");File.WriteAllText(duplicate,wire.TrimEnd().TrimEnd('}')+",\"Ver\\u0073ion\":1}");
  denied=false;try{new SettingsStore(duplicate).Load();}catch(InvalidOperationException){denied=true;}catch(InvalidDataException){denied=true;}
  check(denied,"settings escaped duplicate envelope key rejected");
  var badStore=new SettingsStore(bad);try{badStore.Load();}catch(Exception){}
  denied=false;try{badStore.Save(state);}catch(InvalidOperationException){denied=true;}catch(InvalidDataException){denied=true;}
  check(denied&&File.ReadAllText(bad)=="not JSON","settings malformed instance blocks later overwrite");
  var legacy=new SettingsState{ProfileJson=profileJson,IsRegistered=true,DeviceId=new string('a',32),BearerToken=client.Token,TemporaryPassword="0123456789ABCDEF",PasswordExpiresAt=client.PasswordExpiresAt,BoundServerUrl=client.ServerUrl,BoundCertificateSha256=client.CertificateSha256};
  var legacyStore=new SettingsStore(Path.Combine(dir,"legacy-settings.json"));legacyStore.Save(legacy);var legacyLoaded=legacyStore.Load();
  check(legacyLoaded.DeviceId==legacy.DeviceId&&legacyLoaded.TemporaryPassword==legacy.TemporaryPassword,"legacy ID32 password16 DPAPI compatible load without online validation");
  store.ClearCredentials();loaded=store.Load();outer=json.Deserialize<Dictionary<string,object>>(File.ReadAllText(path));
  check(!loaded.IsRegistered&&loaded.ProfileJson==profileJson&&outer["ProtectedCredentials"]==null,"settings reset atomically retains profile without credentials");
 }
}
