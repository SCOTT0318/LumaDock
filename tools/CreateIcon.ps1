$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') {
    & (Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe') -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath
    if ($LASTEXITCODE -ne 0) { throw 'Icon generation failed.' }
    return
}
$iconRoot = Split-Path -Parent $PSScriptRoot
New-Item -ItemType Directory -Path (Join-Path $iconRoot 'assets') -Force | Out-Null
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Collections.Generic;
public static class MonitorIconBuilder {
    private static GraphicsPath Round(float x,float y,float w,float h,float r) {
        var p=new GraphicsPath(); float d=r*2;
        p.AddArc(x,y,d,d,180,90); p.AddArc(x+w-d,y,d,d,270,90);
        p.AddArc(x+w-d,y+h-d,d,d,0,90); p.AddArc(x,y+h-d,d,d,90,90); p.CloseFigure(); return p;
    }
    private static Bitmap Draw(int size) {
        var image=new Bitmap(size,size,PixelFormat.Format32bppArgb);
        using(var g=Graphics.FromImage(image)) {
            g.SmoothingMode=SmoothingMode.AntiAlias; g.Clear(Color.Transparent); g.ScaleTransform(size/256f,size/256f);
            using(var bg=new SolidBrush(Color.FromArgb(17,28,40))) using(var p=Round(8,8,240,240,58)) g.FillPath(bg,p);
            using(var face=new SolidBrush(Color.FromArgb(30,61,66))) using(var outline=new Pen(Color.FromArgb(140,232,211),12)) using(var p=Round(44,54,168,126,20)) {g.FillPath(face,p);g.DrawPath(outline,p);}
            using(var accent=new SolidBrush(Color.FromArgb(140,232,211))) g.FillPie(accent,98,87,60,60,90,180);
            using(var pen=new Pen(Color.FromArgb(140,232,211),6)) g.DrawArc(pen,98,87,60,60,270,180);
            using(var pen=new Pen(Color.FromArgb(140,232,211),12)) {pen.StartCap=LineCap.Round;pen.EndCap=LineCap.Round;g.DrawLine(pen,128,182,128,206);g.DrawLine(pen,94,211,162,211);}
        } return image;
    }
    public static void Create(string output,string preview) {
        int[] sizes={16,20,24,32,40,48,64,128,256}; var images=new List<byte[]>();
        foreach(int size in sizes) using(var bitmap=Draw(size)) using(var memory=new MemoryStream()) {bitmap.Save(memory,ImageFormat.Png);images.Add(memory.ToArray());if(size==256) bitmap.Save(preview,ImageFormat.Png);}
        using(var stream=File.Create(output)) using(var writer=new BinaryWriter(stream)) {
            writer.Write((ushort)0);writer.Write((ushort)1);writer.Write((ushort)sizes.Length);int offset=6+16*sizes.Length;
            for(int i=0;i<sizes.Length;i++){writer.Write((byte)(sizes[i]==256?0:sizes[i]));writer.Write((byte)(sizes[i]==256?0:sizes[i]));writer.Write((byte)0);writer.Write((byte)0);writer.Write((ushort)1);writer.Write((ushort)32);writer.Write(images[i].Length);writer.Write(offset);offset+=images[i].Length;}
            foreach(var data in images) writer.Write(data);
        }
    }
}
'@
[MonitorIconBuilder]::Create((Join-Path $iconRoot 'assets\MonitorControl.ico'), (Join-Path $iconRoot 'assets\MonitorControl.png'))
Write-Output 'Created multi-resolution monitor icon (16-256 px).'
