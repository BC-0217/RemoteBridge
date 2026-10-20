using System;
using System.Threading.Tasks;
using PeerDesk.Windows;
internal static class Program
{
    private static int count;
    private static void Check(bool condition, string name) { if(!condition) throw new Exception(name); Console.WriteLine("PASS "+name); count++; }
    private static bool Reject(Action action) { try { action(); return false; } catch(ArgumentOutOfRangeException) { return true; } }
    private static void Bad(AdaptiveQualityPolicy p) { p.Observe(70,250,450000,150); }
    private static void Good(AdaptiveQualityPolicy p) { p.Observe(10,40,50000,0); }
    private static int Main()
    {
        try {
            var p=new AdaptiveQualityPolicy();
            Check(p.Snapshot().Level==1,"auto starts balanced");
            Bad(p); Bad(p); Check(p.Snapshot().Level==1,"single spikes do not oscillate");
            Bad(p); Check(p.Snapshot().Level==2,"three sustained bad samples downshift");
            for(int i=0;i<9;i++)Bad(p); Check(p.Snapshot().Level==3,"lower quality bounded");
            for(int i=0;i<29;i++)Good(p); Check(p.Snapshot().Level==3,"recovery waits thirty healthy samples");
            Good(p); Check(p.Snapshot().Level==2,"sustained healthy recovery");
            for(int i=0;i<90;i++)Good(p); Check(p.Snapshot().Level==0,"upper quality bounded");
            Check(p.Snapshot().MaxWidth==1920 && p.Snapshot().MaxHeight==1080 && p.Snapshot().IntervalMs>=50,"wire dimensions and cadence bounds");
            p.SetMode(QualityMode.Sharp); for(int i=0;i<9;i++)Bad(p);
            Check(p.Snapshot().JpegQuality==85,"sharp choice not silently adapted");
            p.SetMode(QualityMode.Fast); for(int i=0;i<90;i++)Good(p);
            Check(p.Snapshot().MaxWidth==960 && p.Snapshot().IntervalMs==100,"fast choice stable");
            p.SetMode(QualityMode.Auto); Check(p.Snapshot().Level==1,"mode switch discards previous session sample history");
            Check(Reject(()=>p.SetMode((QualityMode)9)),"unknown mode rejected");
            Check(Reject(()=>p.Observe(Double.NaN,0,1,0)),"NaN rejected");
            Check(Reject(()=>p.Observe(0,Double.PositiveInfinity,1,0)),"infinity rejected");
            Check(Reject(()=>p.Observe(-1,0,1,0)),"negative sample rejected");
            Check(Reject(()=>p.Observe(0,0,524289,0)),"oversize frame rejected");
            Check(Reject(()=>p.Observe(0,0,0,0)),"empty frame rejected");
            Check(Reject(()=>p.Observe(0,0,1,60001)),"unbounded queue age rejected");
            Parallel.For(0,1000,i=> { p.Observe(10,40,50000,0); var s=p.Snapshot(); if(s.Level<0||s.Level>3)throw new Exception("concurrent invalid"); });
            Check(p.Snapshot().Level==0,"concurrent observes remain bounded");
            var expired=new AdaptiveQualityPolicy();
            expired.ObserveExpiredFrame(); Check(expired.Snapshot().Level==2,"expired frame immediately downshifts auto");
            expired.ObserveExpiredFrame(); Check(expired.Snapshot().Level==3,"second expired frame reaches lowest quality");
            expired.ObserveExpiredFrame(); Check(expired.Snapshot().Level==3,"expired frame quality floor bounded");
            expired.SetMode(QualityMode.Sharp); expired.ObserveExpiredFrame();
            Check(expired.Snapshot().Level==0,"expired frame preserves sharp mode");
            expired.SetMode(QualityMode.Fast); expired.ObserveExpiredFrame();
            Check(expired.Snapshot().Level==3,"expired frame preserves fast mode");
            expired.SetMode(QualityMode.Auto); for(int i=0;i<29;i++)Good(expired);
            expired.ObserveExpiredFrame(); Good(expired);
            Check(expired.Snapshot().Level==2,"expired frame resets prior healthy streak");
            Bad(expired); Bad(expired);
            Check(expired.Snapshot().Level==2,"expired frame resets EWMA before new observations");
            Console.WriteLine("ADAPTIVE_QUALITY_PASS count="+count);return 0;
        } catch(Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
