[CmdletBinding()]
param([string]$Path = 'D:\demo')
$ErrorActionPreference = 'Stop'
Add-Type @'
using System;
using System.Runtime.InteropServices;
using System.Text;
[ComImport, Guid("0C6C4200-C589-11D0-999A-00C04FD655E1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IOverlay {
    [PreserveSig] int IsMemberOf([MarshalAs(UnmanagedType.LPWStr)] string path, uint attributes);
    [PreserveSig] int GetOverlayInfo([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int size, out int index, out uint flags);
    [PreserveSig] int GetPriority(out int priority);
}
public static class OverlayProbe {
    public static void ValidatePixels(string file, int rgb) {
        using(var stream=System.IO.File.OpenRead(file))
        using(var reader=new System.IO.BinaryReader(stream)) {
            if(reader.ReadUInt16()!=0 || reader.ReadUInt16()!=1) throw new Exception("Invalid ICO header");
            int count=reader.ReadUInt16(); if(count<6) throw new Exception("Missing DPI icon frames");
            for(int i=0;i<count;i++) {
                stream.Position=6+16*i+8;
                int bytes=reader.ReadInt32(), offset=reader.ReadInt32();
                stream.Position=offset;
                using(var png=new System.IO.MemoryStream(reader.ReadBytes(bytes)))
                using(var image=new System.Drawing.Bitmap(png)) {
                    var center=image.GetPixel(image.Width/4,image.Height*3/4);
                    if(center.A!=255 || (center.ToArgb()&0xffffff)!=rgb) throw new Exception("Dot center differs from preset RGB");
                    for(int y=0;y<image.Height;y++) for(int x=0;x<image.Width;x++) {
                        var color=image.GetPixel(x,y);
                        if(color.A>0 && color.R==255 && color.G==255 && color.B==255) throw new Exception("White outline pixel found");
                    }
                }
            }
        }
    }
    public static int Member(object handler, string path) { return ((IOverlay)handler).IsMemberOf(path,0); }
    public static string Icon(object handler) {
        var file = new StringBuilder(512); int index; uint flags;
        int hr=((IOverlay)handler).GetOverlayInfo(file,512,out index,out flags);
        if(hr!=0) Marshal.ThrowExceptionForHR(hr); return file.ToString();
    }
    public static IOverlay Create(int slot) {
        var guid = new Guid("7d619ac" + slot + "-dbe2-4f33-9a11-6cc2c8329001");
        return (IOverlay)Activator.CreateInstance(Type.GetTypeFromCLSID(guid));
    }
}
'@ -ReferencedAssemblies System.Drawing
$handlers = @(0..6 | ForEach-Object { [OverlayProbe]::Create($_) })
$paletteText = [IO.File]::ReadAllText((Join-Path (Split-Path $PSScriptRoot -Parent) 'src\PolyMount.Core\Core\OriginPalette.inc'))
$palette = @([regex]::Matches($paletteText,'0x([0-9A-F]{6})') | ForEach-Object { [Convert]::ToInt32($_.Groups[1].Value,16) })
for($i=0; $i -lt $handlers.Count; $i++) {
    $handler=$handlers[$i]
    $file = [OverlayProbe]::Icon($handler)
    if (-not (Test-Path -LiteralPath $file)) { throw "Icon missing: $file" }
    [OverlayProbe]::ValidatePixels($file,$palette[$i])
    if ([OverlayProbe]::Member($handler,'C:\Windows') -eq 0) { throw 'Non-mount path received an overlay' }
}
$deadline=[DateTime]::UtcNow.AddSeconds(20)
$slot=-1
do {
    for($i=0;$i -lt 7;$i++) { if ([OverlayProbe]::Member($handlers[$i],$Path) -eq 0) { $slot=$i;break } }
    if ($slot -lt 0) { Start-Sleep -Milliseconds 300 }
} while($slot -lt 0 -and [DateTime]::UtcNow -lt $deadline)
if ($slot -lt 0) { throw "No origin overlay resolved for $Path" }
Write-Host "COM overlay passed: $Path -> palette slot $slot; all seven icons exist; non-mount paths ignored."
