using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace PeerDesk.Windows {
    internal sealed class UiLanguage {
        internal readonly string Code,Name;
        internal UiLanguage(string code,string name){Code=code;Name=name;}
        public override string ToString(){return Name;}
    }
    // Presentation only. Original identifiers, passwords, protocol state and editable values are never translated.
    internal static class UiLocalization {
        internal static readonly UiLanguage[] Languages={
            new UiLanguage("ko","한국어"),new UiLanguage("en","English"),new UiLanguage("ja","日本語"),
            new UiLanguage("zh-CN","简体中文"),new UiLanguage("ru","Русский"),new UiLanguage("fr","Français"),new UiLanguage("hi","हिन्दी")
        };
        private static readonly Dictionary<string,Dictionary<string,string>> catalogs=Load();
        private static readonly Dictionary<string,string> sourceKeys=BuildSources();
        private static readonly Dictionary<string,string> reverseSources=BuildReverse();
        private sealed class Binding {internal string Source;internal bool Writing;internal object[] Arguments;internal EventHandler Handler;}
        private static readonly ConditionalWeakTable<Control,Binding> bindings=new ConditionalWeakTable<Control,Binding>();
        internal static string Language {get;private set;}="ko";
        internal static bool Dark {get;private set;}
        internal static event Action AppearanceChanged;
        internal static CultureInfo Culture {get{return CultureInfo.GetCultureInfo(Language);}}
        internal static void Select(string language,bool dark){
            if(!catalogs.ContainsKey(language))throw new ArgumentException("Unsupported language.");
            if(Language==language&&Dark==dark)return;
            Language=language;Dark=dark;
            foreach(Form form in Application.OpenForms.Cast<Form>().ToArray()){
                if(form.IsDisposed)continue;
                if(form.InvokeRequired){try{form.BeginInvoke(new Action(()=>Apply(form)));}catch(InvalidOperationException){}}
                else Apply(form);
            }
            var changed=AppearanceChanged;if(changed!=null)changed();
        }
        internal static string Text(string source){
            if(source==null)return "";
            string key;if(!sourceKeys.TryGetValue(source,out key))key=source;
            string translated;return catalogs[Language].TryGetValue(key,out translated)?translated:source;
        }
        private static string Canonical(string text){
            if(sourceKeys.ContainsKey(text))return text;
            string original;return reverseSources.TryGetValue(text,out original)?original:text;
        }
        internal static string Format(string source,params object[] args){return String.Format(Culture,Text(source),args);}
        internal static string LocalizeError(Exception error){string message=error.Message??"";string translated=Text(message);return translated!=message||sourceKeys.ContainsKey(message)?translated:Text("작업을 완료하지 못했습니다. 연결 상태와 입력값을 확인해 주세요.")+Environment.NewLine+message+" ("+error.GetType().Name+")";}
        internal static void Bind(Control control,string source){
            if(control==null||control.IsDisposed)return;
            var binding=bindings.GetValue(control,c=>{
                var b=new Binding{Source=Canonical(source)};
                b.Handler=(s,e)=>{if(b.Writing)return;b.Source=Canonical(c.Text);b.Arguments=null;Render(c,b);};c.TextChanged+=b.Handler;
                return b;
            });
            binding.Source=Canonical(source);binding.Arguments=null;Render(control,binding);
        }
        internal static void Unbind(Control control){Binding b;if(control!=null&&bindings.TryGetValue(control,out b)){control.TextChanged-=b.Handler;bindings.Remove(control);}}
        internal static void BindFormat(Control control,string source,params object[] args){
            Bind(control,source);var b=bindings.GetValue(control,c=>new Binding());b.Source=source;b.Arguments=args;Render(control,b);
        }
        internal static void Observe(Control control,Action refresh){
            Action changed=()=>{if(control.IsDisposed)return;if(control.InvokeRequired){try{control.BeginInvoke(refresh);}catch(InvalidOperationException){}}else refresh();};
            AppearanceChanged+=changed;control.Disposed+=(s,e)=>AppearanceChanged-=changed;
        }
        private static void Render(Control control,Binding binding){
            if(binding.Writing||control.IsDisposed)return;
            binding.Writing=true;
            try{string next=binding.Arguments==null?Text(binding.Source):Format(binding.Source,binding.Arguments);if(control.Text!=next)control.Text=next;}finally{binding.Writing=false;}
        }
        internal static void Attach(Form form){if(form==null)return;form.Shown+=(s,e)=>Apply(form);Apply(form);}
        internal static void Apply(Control root){
            if(root==null||root.IsDisposed)return;
            var controls=new List<Control>();Collect(root,controls);foreach(var control in controls)control.SuspendLayout();
            try{ApplyText(root);UiTheme.Apply(root,Dark);}
            finally{for(int i=controls.Count-1;i>=0;i--)if(!controls[i].IsDisposed)controls[i].ResumeLayout(true);}
            root.Invalidate(true);
        }
        private static void Collect(Control root,List<Control> controls){controls.Add(root);foreach(Control child in root.Controls)Collect(child,controls);}
        private static void ApplyText(Control root){
            if(root==null||root.IsDisposed)return;
            Binding binding;
            if(bindings.TryGetValue(root,out binding))Render(root,binding);
            else if(root is Label||root is ButtonBase||root is GroupBox||root is TabPage||root is Form)Bind(root,root.Text);
            if(!String.IsNullOrEmpty(root.AccessibleName))root.AccessibleName=Text(Canonical(root.AccessibleName));
            WireList(root as ListControl);
            var strip=root as ToolStrip;if(strip!=null)foreach(ToolStripItem item in strip.Items)TranslateItem(item);
            if(root.ContextMenuStrip!=null)foreach(ToolStripItem item in root.ContextMenuStrip.Items)TranslateItem(item);
            foreach(Control child in root.Controls)ApplyText(child);
        }
        private static readonly ConditionalWeakTable<ListControl,object> drawnLists=new ConditionalWeakTable<ListControl,object>();
        private static void WireList(ListControl list){
            if(list==null)return;object marker;if(drawnLists.TryGetValue(list,out marker))return;
            var combo=list as ComboBox;var box=list as ListBox;
            if(combo!=null&&combo.DrawMode!=DrawMode.Normal||box!=null&&box.DrawMode!=DrawMode.Normal)return;
            DrawItemEventHandler draw=(s,e)=>{
                if(e.Index<0)return;var control=(ListControl)s;object item=combo!=null?combo.Items[e.Index]:box.Items[e.Index];
                e.DrawBackground();string text=item is UiLanguage?item.ToString():Text(Canonical(item==null?"":item.ToString()));
                TextRenderer.DrawText(e.Graphics,text,control.Font,e.Bounds,e.ForeColor,TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis);e.DrawFocusRectangle();
            };
            if(combo!=null){combo.DrawMode=DrawMode.OwnerDrawFixed;combo.ItemHeight=combo.Font.Height+4;combo.DrawItem+=draw;combo.FontChanged+=(s,e)=>combo.ItemHeight=combo.Font.Height+4;}
            if(box!=null){box.DrawMode=DrawMode.OwnerDrawFixed;box.ItemHeight=box.Font.Height+4;box.DrawItem+=draw;box.FontChanged+=(s,e)=>box.ItemHeight=box.Font.Height+4;}
            drawnLists.Add(list,new object());
        }
        private static readonly ConditionalWeakTable<ToolStripItem,Binding> menuBindings=new ConditionalWeakTable<ToolStripItem,Binding>();
        private static void TranslateItem(ToolStripItem item){
            var b=menuBindings.GetValue(item,k=>new Binding{Source=Canonical(k.Text)});item.Text=Text(b.Source);
            var menu=item as ToolStripDropDownItem;if(menu!=null)foreach(ToolStripItem child in menu.DropDownItems)TranslateItem(child);
        }
        private static Dictionary<string,string> BuildReverse(){
            var result=new Dictionary<string,string>(StringComparer.Ordinal);
            foreach(var language in Languages)foreach(var pair in catalogs[language.Code]){
                string original;if(catalogs["ko"].TryGetValue(pair.Key,out original)&&!result.ContainsKey(pair.Value))result.Add(pair.Value,original);
            }return result;
        }
        private static Dictionary<string,string> BuildSources(){
            var result=new Dictionary<string,string>(StringComparer.Ordinal);foreach(var pair in catalogs["ko"])if(!result.ContainsKey(pair.Value))result.Add(pair.Value,pair.Key);return result;
        }
        private static Dictionary<string,Dictionary<string,string>> Load(){
            var result=new Dictionary<string,Dictionary<string,string>>(StringComparer.Ordinal);
            var json=new JavaScriptSerializer{MaxJsonLength=1024*1024};
            foreach(var language in Languages){
                string name="PeerDesk.Windows.Locales."+language.Code+".json";
                using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream(name)){
                    if(stream==null){result.Add(language.Code,new Dictionary<string,string>());continue;}
                    using(var reader=new System.IO.StreamReader(stream,System.Text.Encoding.UTF8,true))result.Add(language.Code,json.Deserialize<Dictionary<string,string>>(reader.ReadToEnd()));
                }
            }
            return result;
        }
    }
}
