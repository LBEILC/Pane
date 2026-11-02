using System.Numerics;
using System.Runtime.InteropServices;
using Pane.Settings;
namespace Pane.Services.Icons;

internal enum IconKind { Raw, Full, Plate, Neutral }
internal sealed record IconAnalysis(byte[] Pixels,int X,int Y,int Width,int Height,double Aspect,double Fill,double Luminance,double Saturation,bool Empty=false);
internal static class IconRenderer
{
    public const int Size=128;
    [DllImport("Pane.Native.dll",CharSet=CharSet.Unicode,CallingConvention=CallingConvention.Cdecl)]
    static extern int PaneIconDecode(string path,[Out] byte[] pixels);
    [DllImport("Pane.Native.dll",CharSet=CharSet.Unicode,CallingConvention=CallingConvention.Cdecl)]
    static extern int PaneIconEncode(string path,byte[] pixels,int size);
    public static IconAnalysis Load(string path){var pixels=new byte[256*256*4];if(PaneIconDecode(path,pixels)!=1)throw new IOException("图标解码失败");return Analyze(pixels);} // 不翻译：异常
    public static void Save(string path,byte[] pixels){if(PaneIconEncode(path,pixels,Size)!=1)throw new IOException("图标编码失败");} // 不翻译：异常
    public static IconAnalysis Analyze(byte[] d){
        d=RemoveFaintFrame(d);
        int x0=256,y0=256,x1=-1,y1=-1;
        for(int y=0;y<256;y++)for(int x=0;x<256;x++)if(d[(y*256+x)*4+3]>24){x0=Math.Min(x0,x);y0=Math.Min(y0,y);x1=Math.Max(x1,x);y1=Math.Max(y1,y);}
        if(x1<0)return new(d,0,0,256,256,1,0,.5,0,true);
        int w=x1-x0+1,h=y1-y0+1,side=Math.Min(w,h),inside=0,solid=0;double sx=x0+(w-side)/2.0,sy=y0+(h-side)/2.0;
        for(int y=0;y<side;y+=2)for(int x=0;x<side;x+=2){double u=x/(double)side*2-1,v=y/(double)side*2-1;if(Math.Pow(Math.Abs(u),5)+Math.Pow(Math.Abs(v),5)>.92)continue;inside++;if(d[((int)(sy+y)*256+(int)(sx+x))*4+3]>200)solid++;}
        int n=0;double lum=0,sat=0;
        for(int i=0;i<d.Length;i+=16){if(d[i+3]<128)continue;double r=d[i]/255.0,g=d[i+1]/255.0,b=d[i+2]/255.0,mx=Math.Max(r,Math.Max(g,b)),mn=Math.Min(r,Math.Min(g,b));lum+=.2126*r+.7152*g+.0722*b;sat+=mx>0?(mx-mn)/mx:0;n++;}
        return new(d,x0,y0,w,h,w/(double)h,inside>0?solid/(double)inside:0,n>0?lum/n:.5,n>0?sat/n:0);
    }
    // 只移除独立的大矩形细框：跨度至少 128、四边至少 90% 连续。
    // 普通框至多 2px；Shell 实测的 2px 主边加阴影共 5px；整体 alpha 至多 80。
    // 边框必须与内部内容隔开至少 2px，较宽低透明填充及实质边框保持原样。
    static byte[] RemoveFaintFrame(byte[] pixels){
        int left=256,top=256,right=-1,bottom=-1;
        for(int y=0;y<256;y++)for(int x=0;x<256;x++)if(pixels[(y*256+x)*4+3]>24){left=Math.Min(left,x);top=Math.Min(top,y);right=Math.Max(right,x);bottom=Math.Max(bottom,y);}
        if(right-left<127||bottom-top<127)return pixels;
        int topCount=0,bottomCount=0,leftCount=0,rightCount=0,thickness=0;bool content=false;
        for(int y=top;y<=bottom;y++)for(int x=left;x<=right;x++){
            byte a=pixels[(y*256+x)*4+3];int edge=Math.Min(Math.Min(x-left,right-x),Math.Min(y-top,bottom-y));
            if(edge<5&&a>80||edge>=5&&edge<7&&a>24)return pixels;
            if(edge<5&&a>24)thickness=Math.Max(thickness,edge+1);
            if(edge>=7&&a>80)content=true;
            if(a>24){if(y==top)topCount++;if(y==bottom)bottomCount++;if(x==left)leftCount++;if(x==right)rightCount++;}
        }
        if(!content||Math.Min(topCount,bottomCount)<(right-left+1)*.9||Math.Min(leftCount,rightCount)<(bottom-top+1)*.9)return pixels;
        // 宽于 2px 的框必须在四条直边中部都有 1～2px 较强主边，排除宽透明描边。
        if(thickness>2){
            int cx=(left+right)/2,cy=(top+bottom)/2;
            foreach(var line in new[]{Enumerable.Range(0,thickness).Select(i=>(left+i,cy)),Enumerable.Range(0,thickness).Select(i=>(right-i,cy)),Enumerable.Range(0,thickness).Select(i=>(cx,top+i)),Enumerable.Range(0,thickness).Select(i=>(cx,bottom-i))}){
                int strong=line.Count(p=>pixels[(p.Item2*256+p.Item1)*4+3]>48);if(strong<1||strong>2)return pixels;
            }
        }
        var clean=(byte[])pixels.Clone();for(int y=top;y<=bottom;y++)for(int x=left;x<=right;x++)if(Math.Min(Math.Min(x-left,right-x),Math.Min(y-top,bottom-y))<thickness)clean[(y*256+x)*4+3]=0;
        return clean;
    }
    public static IconKind Decide(IconAnalysis a,IconMode mode)=>mode==IconMode.Raw?IconKind.Raw:mode!=IconMode.ForceMask&&a.Aspect>.85&&a.Aspect<1.18&&a.Fill>=.9?IconKind.Full:a.Saturation<.16?IconKind.Neutral:IconKind.Plate;
    static Vector4 Pixel(byte[] d,int x,int y){if(x<0||y<0||x>=256||y>=256)return default;int i=(y*256+x)*4;float a=d[i+3]/255f;return new(d[i]/255f*a,d[i+1]/255f*a,d[i+2]/255f*a,a);}
    static Vector4 Sample(IconAnalysis a,double x,double y){int ix=(int)Math.Floor(x),iy=(int)Math.Floor(y);float fx=(float)(x-ix),fy=(float)(y-iy);return Vector4.Lerp(Vector4.Lerp(Pixel(a.Pixels,ix,iy),Pixel(a.Pixels,ix+1,iy),fx),Vector4.Lerp(Pixel(a.Pixels,ix,iy+1),Pixel(a.Pixels,ix+1,iy+1),fx),fy);}
    static Vector4 Draw(IconAnalysis a,double x,double y,double scale,double offsetY=0){double w=a.Width*scale,h=a.Height*scale,left=(Size-w)/2,top=(Size-h)/2+offsetY;if(x<left||y<top||x>=left+w||y>=top+h)return default;return Sample(a,a.X+(x-left)/scale-.5,a.Y+(y-top)/scale-.5);}
    static Vector4 Over(Vector4 front,Vector4 back)=>front+back*(1-front.W);
    static Vector4 Color(int r,int g,int b,float alpha=1)=>new(r/255f*alpha,g/255f*alpha,b/255f*alpha,alpha);
    // 分离高斯卷积，保留画布外三倍标准差范围；透明区域使用预乘 alpha。
    static Vector4[] Blur(Func<double,double,Vector4> draw,double sigma){
        int radius=(int)Math.Ceiling(sigma*3),w=Size+radius*2;var kernel=new float[radius*2+1];double sum=0;
        for(int i=-radius;i<=radius;i++){double k=Math.Exp(-i*i/(2*sigma*sigma));kernel[i+radius]=(float)k;sum+=k;}
        for(int i=0;i<kernel.Length;i++)kernel[i]/=(float)sum;
        var source=new Vector4[w*w];for(int y=0;y<w;y++)for(int x=0;x<w;x++)source[y*w+x]=draw(x-radius+.5,y-radius+.5);
        var horizontal=new Vector4[Size*w];for(int y=0;y<w;y++)for(int x=0;x<Size;x++){Vector4 v=default;for(int k=0;k<kernel.Length;k++)v+=source[y*w+x+k]*kernel[k];horizontal[y*Size+x]=v;}
        var result=new Vector4[Size*Size];for(int y=0;y<Size;y++)for(int x=0;x<Size;x++){Vector4 v=default;for(int k=0;k<kernel.Length;k++)v+=horizontal[(y+k)*Size+x]*kernel[k];result[y*Size+x]=v;}return result;
    }
    // 与原型相同的 96 段路径；四倍子像素覆盖用于裁剪及一像素描边。
    static Vector2[] Outline(double inset){var p=new Vector2[96];double r=(Size-2*inset)/2;for(int i=0;i<96;i++){double t=i*Math.PI*2/96,c=Math.Cos(t),s=Math.Sin(t);p[i]=new((float)(Size/2.0+r*Math.Sign(c)*Math.Pow(Math.Abs(c),.4)),(float)(Size/2.0+r*Math.Sign(s)*Math.Pow(Math.Abs(s),.4)));}return p;}
    static bool Inside(Vector2 p,Vector2[] path){bool inside=false;for(int i=0,j=path.Length-1;i<path.Length;j=i++){var a=path[i];var b=path[j];if((a.Y>p.Y)!=(b.Y>p.Y)&&p.X<(b.X-a.X)*(p.Y-a.Y)/(b.Y-a.Y)+a.X)inside=!inside;}return inside;}
    static float Distance(Vector2 p,Vector2[] path){float d=float.MaxValue;for(int i=0;i<path.Length;i++){var a=path[i];var v=path[(i+1)%path.Length]-a;float t=Math.Clamp(Vector2.Dot(p-a,v)/v.LengthSquared(),0,1);d=Math.Min(d,Vector2.DistanceSquared(p,a+v*t));}return MathF.Sqrt(d);}
    static readonly (float[] Clip,float[] Stroke) coverage=Coverage();
    static (float[],float[]) Coverage(){var clip=new float[Size*Size];var stroke=new float[Size*Size];var outer=Outline(0);var edge=Outline(.5);for(int y=0;y<Size;y++)for(int x=0;x<Size;x++)for(int sy=0;sy<4;sy++)for(int sx=0;sx<4;sx++){var p=new Vector2(x+(sx+.5f)/4,y+(sy+.5f)/4);if(Inside(p,outer))clip[y*Size+x]+=1/16f;if(Distance(p,edge)<=.5f)stroke[y*Size+x]+=1/16f;}return(clip,stroke);}
    public static byte[] Render(IconAnalysis a,IconKind kind,bool dark){
        var output=new byte[Size*Size*4];if(a.Empty)return output;
        Vector4[]? background=null,shadow=null;double scale=Size*.74/Math.Max(a.Width,a.Height);
        if(kind==IconKind.Plate)background=Blur((x,y)=>Draw(a,x,y,Size*1.25/Math.Min(a.Width,a.Height)),Size*.3);
        if(kind is IconKind.Plate or IconKind.Neutral)shadow=Blur((x,y)=>new Vector4(0,0,0,Draw(a,x,y,scale,Size*.025).W*.35f),Size*.08/2);
        for(int y=0;y<Size;y++)for(int x=0;x<Size;x++){
            int i=y*Size+x;Vector4 v;
            if(kind==IconKind.Raw)v=Draw(a,x+.5,y+.5,Size/(double)Math.Max(a.Width,a.Height));
            else if(kind==IconKind.Full){double side=Math.Min(a.Width,a.Height);v=Sample(a,a.X+(a.Width-side)/2+(x+.5)*side/Size-.5,a.Y+(a.Height-side)/2+(y+.5)*side/Size-.5);}
            else {
                if(background!=null){v=background[i];float l=.213f*v.X+.715f*v.Y+.072f*v.Z;v=new(Math.Clamp(l+(v.X-l)*1.6f,0,v.W),Math.Clamp(l+(v.Y-l)*1.6f,0,v.W),Math.Clamp(l+(v.Z-l)*1.6f,0,v.W),v.W);v=Over(dark?Color(12,12,16,.42f):Color(255,255,255,.336f),v);}
                else v=a.Luminance<.45?(dark?Color(217,217,224):Color(241,241,244)):(dark?Color(58,58,66):Color(74,74,84));
                v=Over(Draw(a,x+.5,y+.5,scale),Over(shadow![i],v));
            }
            if(kind!=IconKind.Raw){v*=coverage.Clip[i];double t=(y+.5)/Size;float alpha=(float)(t<.5?.38+(.08-.38)*t*2:.08+(.16-.08)*(t-.5)*2);v=Over(new Vector4(alpha*coverage.Stroke[i]),v);}
            if(v.W>0){output[i*4]=(byte)Math.Clamp(Math.Round(v.X/v.W*255),0,255);output[i*4+1]=(byte)Math.Clamp(Math.Round(v.Y/v.W*255),0,255);output[i*4+2]=(byte)Math.Clamp(Math.Round(v.Z/v.W*255),0,255);output[i*4+3]=(byte)Math.Clamp(Math.Round(v.W*255),0,255);}
        }return output;
    }
}
