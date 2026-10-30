$dll=(Join-Path $root 'bin/native/Pane.Capture.dll').Replace('\','/')
if(!(Test-Path -LiteralPath $dll)){throw '缺少 Pane.Capture.dll，请先运行 build.ps1 或 native.ps1'}
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @"
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
public static class DesktopDxgi {
 [DllImport("$dll",ExactSpelling=true)] static extern int CaptureDesktop(int x,int y,int w,int h,[Out] byte[] pixels,int length);
 public static Bitmap Capture(int x,int y,int w,int h) {
  var data=new byte[checked(w*h*4)];Marshal.ThrowExceptionForHR(CaptureDesktop(x,y,w,h,data,data.Length));
  var bitmap=new Bitmap(w,h,PixelFormat.Format32bppArgb);
  try {
   var bits=bitmap.LockBits(new Rectangle(0,0,w,h),ImageLockMode.WriteOnly,PixelFormat.Format32bppArgb);
   try {for(int row=0;row<h;row++)Marshal.Copy(data,row*w*4,IntPtr.Add(bits.Scan0,row*bits.Stride),w*4);} finally {bitmap.UnlockBits(bits);}
   return bitmap;
  } catch {bitmap.Dispose();throw;}
 }
}
"@
