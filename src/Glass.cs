using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace Islet
{
    // Liquid-glass material. The input is a snapshot of whatever is behind the
    // island; the shader masks it to the island shape (a rounded rect smoothly
    // unioned with an optional bubble), blurs it, bends it near the rim like a
    // lens, adds chromatic fringing and a specular rim, and tints it adaptively
    // so white text stays readable over bright pages. Runs on the GPU.
    public sealed class GlassEffect : ShaderEffect
    {
        private const string Source = @"
sampler2D Input : register(s0);
float4 Island  : register(c0); // left, top, width, height (DIP)
float4 Bubble  : register(c1); // centre x, centre y, radius, merge radius
float4 Surface : register(c2); // width, height, 1/width, 1/height
float4 Style   : register(c3); // corner radius, blur, rim width, highlight
float4 Tint    : register(c4); // rgb, base strength

float sdIsland(float2 p)
{
    float2 hs = Island.zw * 0.5;
    float r = min(Style.x, min(hs.x, hs.y));
    float2 q = abs(p - (Island.xy + hs)) - hs + r;
    return length(max(q, 0)) + min(max(q.x, q.y), 0) - r;
}

float sdf(float2 p)
{
    float a = sdIsland(p);
    float b = length(p - Bubble.xy) - Bubble.z;
    float k = Bubble.w;
    float h = saturate(0.5 + 0.5 * (b - a) / k);
    float merged = lerp(b, a, h) - k * h * (1 - h);
    return Bubble.z > 0.25 ? merged : a;
}

float3 sampleAt(float2 p)
{
    return tex2D(Input, saturate(p * Surface.zw)).rgb;
}

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float2 p = uv * Surface.xy;
    float d = sdf(p);
    float alpha = saturate(0.5 - d * 1.2);
    if (alpha <= 0) return 0;

    float2 n = float2(sdf(p + float2(1, 0)) - sdf(p - float2(1, 0)),
                      sdf(p + float2(0, 1)) - sdf(p - float2(0, 1)));
    n = n / max(length(n), 1e-4);

    float rim = Style.z;
    float t = saturate(1 + d / rim);       // 0 deep inside, 1 at the edge
    float bend = t * t * t * rim * 1.1;
    float2 sp = p - n * bend;               // look inward near the rim: a lens

    float b = Style.y;
    float3 c = sampleAt(sp) * 0.2;
    c += sampleAt(sp + float2( 1.00,  0.00) * b) * 0.1;
    c += sampleAt(sp + float2(-1.00,  0.00) * b) * 0.1;
    c += sampleAt(sp + float2( 0.00,  1.00) * b) * 0.1;
    c += sampleAt(sp + float2( 0.00, -1.00) * b) * 0.1;
    c += sampleAt(sp + float2( 0.70,  0.70) * b * 1.9) * 0.1;
    c += sampleAt(sp + float2(-0.70,  0.70) * b * 1.9) * 0.1;
    c += sampleAt(sp + float2( 0.70, -0.70) * b * 1.9) * 0.1;
    c += sampleAt(sp + float2(-0.70, -0.70) * b * 1.9) * 0.1;

    // Chromatic fringe where the lens bends hardest.
    float fringe = t * t * 2.5;
    c.r = lerp(c.r, sampleAt(sp - n * fringe).r, t * 0.6);
    c.b = lerp(c.b, sampleAt(sp + n * fringe).b, t * 0.6);

    // Adaptive tint: brighter backdrops get more of it.
    float lum = dot(c, float3(0.299, 0.587, 0.114));
    c = lerp(c, Tint.rgb, saturate(Tint.a + lum * 0.5));

    // Specular rim lit from the top-left, softer counter-light bottom-right.
    float2 light = float2(-0.45, -0.89);
    float spec = pow(saturate(dot(n, light)), 2) + 0.35 * pow(saturate(dot(n, -light)), 3);
    float edgeLine = exp(-abs(d + 0.9) * 1.3);
    c += edgeLine * (0.10 + 0.6 * spec) * Style.w;
    c += 0.035 * t;

    return float4(c * alpha, alpha);
}";

        private static PixelShader shader;
        private static bool shaderFailed;

        public static readonly DependencyProperty InputProperty =
            RegisterPixelShaderSamplerProperty("Input", typeof(GlassEffect), 0);
        public static readonly DependencyProperty IslandProperty = Constant("Island", 0);
        public static readonly DependencyProperty BubbleProperty = Constant("Bubble", 1);
        public static readonly DependencyProperty SurfaceProperty = Constant("Surface", 2);
        public static readonly DependencyProperty StyleProperty = Constant("Style", 3);
        public static readonly DependencyProperty TintProperty = Constant("Tint", 4);

        private static DependencyProperty Constant(string name, int register)
        {
            return DependencyProperty.Register(name, typeof(Point4D), typeof(GlassEffect),
                new UIPropertyMetadata(new Point4D(), PixelShaderConstantCallback(register)));
        }

        private GlassEffect(PixelShader compiled)
        {
            PixelShader = compiled;
            UpdateShaderValue(InputProperty);
            UpdateShaderValue(IslandProperty);
            UpdateShaderValue(BubbleProperty);
            UpdateShaderValue(SurfaceProperty);
            UpdateShaderValue(StyleProperty);
            UpdateShaderValue(TintProperty);
        }

        // Null when the GPU cannot run ps_3_0 (software rendering) or the
        // in-box shader compiler is missing; callers fall back to a flat tint.
        public static GlassEffect TryCreate()
        {
            if (shaderFailed) return null;
            if (shader == null)
            {
                if (RenderCapability.Tier >> 16 < 2 || !RenderCapability.IsPixelShaderVersionSupported(3, 0))
                {
                    shaderFailed = true;
                    return null;
                }
                byte[] bytecode = Compile(Source, "ps_3_0");
                if (bytecode == null)
                {
                    shaderFailed = true;
                    return null;
                }
                shader = new PixelShader();
                shader.SetStreamSource(new MemoryStream(bytecode));
                shader.Freeze();
            }
            return new GlassEffect(shader);
        }

        public Point4D Island { set { SetValue(IslandProperty, value); } }
        public Point4D Bubble { set { SetValue(BubbleProperty, value); } }
        public Point4D Surface { set { SetValue(SurfaceProperty, value); } }
        public Point4D Style { set { SetValue(StyleProperty, value); } }
        public Point4D Tint { set { SetValue(TintProperty, value); } }

        private static byte[] Compile(string source, string target)
        {
            ID3DBlob code = null, errors = null;
            try
            {
                int hr = D3DCompile(source, new IntPtr(source.Length), "glass", IntPtr.Zero, IntPtr.Zero,
                    "main", target, OptimizationLevel3, 0, out code, out errors);
                if (hr < 0 || code == null)
                {
                    string message = errors != null ? Marshal.PtrToStringAnsi(errors.GetBufferPointer()) : "hr=" + hr;
                    Log.Error("glass shader compile failed", new InvalidOperationException(message));
                    return null;
                }
                var bytes = new byte[code.GetBufferSize().ToInt32()];
                Marshal.Copy(code.GetBufferPointer(), bytes, 0, bytes.Length);
                return bytes;
            }
            catch (DllNotFoundException ex)
            {
                Log.Error("d3dcompiler_47.dll unavailable", ex);
                return null;
            }
            finally
            {
                if (code != null) Marshal.ReleaseComObject(code);
                if (errors != null) Marshal.ReleaseComObject(errors);
            }
        }

        private const uint OptimizationLevel3 = 1 << 15;

        [ComImport, Guid("8BA5FB08-5195-40E2-AC58-0D989C3A0102"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ID3DBlob
        {
            [PreserveSig] IntPtr GetBufferPointer();
            [PreserveSig] IntPtr GetBufferSize();
        }

        [DllImport("d3dcompiler_47.dll", CharSet = CharSet.Ansi)]
        private static extern int D3DCompile(string source, IntPtr sourceSize, string sourceName, IntPtr defines,
            IntPtr include, string entryPoint, string target, uint flags1, uint flags2, out ID3DBlob code, out ID3DBlob errors);
    }

    // Copies the screen area behind the overlay into a bitmap the glass samples.
    // The overlay excludes itself from capture, so it never sees its own glass;
    // if that is unavailable the backdrop is only refreshed while hidden.
    internal sealed class Backdrop : IDisposable
    {
        private readonly int width, height;
        private readonly WriteableBitmap bitmap;
        private IntPtr memoryDc, dib, bits;
        private byte[] previous;

        [DllImport("msvcrt.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int memcmp(IntPtr a, byte[] b, UIntPtr count);

        public Backdrop(int width, int height)
        {
            this.width = width;
            this.height = height;
            bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr32, null);
        }

        public ImageSource Image { get { return bitmap; } }

        public bool ExcludesSelf { get; private set; }

        public void ExcludeFromCapture(IntPtr hwnd)
        {
            // Supported for layered windows on Windows 10 2004 and later.
            ExcludesSelf = Native.SetWindowDisplayAffinity(hwnd, Native.WdaExcludeFromCapture);
        }

        // Returns true when the backdrop changed.
        public bool Capture(Native.Rect32 screenRect)
        {
            IntPtr screen = Native.GetDC(IntPtr.Zero);
            if (screen == IntPtr.Zero) return false;
            try
            {
                if (memoryDc == IntPtr.Zero)
                {
                    memoryDc = Native.CreateCompatibleDC(screen);
                    var header = new Native.BitmapInfoHeader
                    {
                        Size = Marshal.SizeOf(typeof(Native.BitmapInfoHeader)),
                        Width = width,
                        Height = -height, // top-down rows
                        Planes = 1,
                        BitCount = 32,
                    };
                    dib = Native.CreateDIBSection(screen, ref header, 0, out bits, IntPtr.Zero, 0);
                    Native.SelectObject(memoryDc, dib);
                    Native.SetStretchBltMode(memoryDc, Native.Halftone);
                    Native.SetBrushOrgEx(memoryDc, 0, 0, IntPtr.Zero);
                }
                if (bits == IntPtr.Zero) return false;
                Native.StretchBlt(memoryDc, 0, 0, width, height, screen, screenRect.Left, screenRect.Top,
                    screenRect.Right - screenRect.Left, screenRect.Bottom - screenRect.Top, Native.SrcCopy);
                // Only hand a new frame to WPF when the backdrop actually changed:
                // an unchanged page costs a memcmp instead of a full glass redraw.
                int size = width * height * 4;
                if (previous != null && memcmp(bits, previous, new UIntPtr((uint)size)) == 0) return false;
                if (previous == null) previous = new byte[size];
                Marshal.Copy(bits, previous, 0, size);
                bitmap.WritePixels(new Int32Rect(0, 0, width, height), bits, size, width * 4);
                return true;
            }
            finally
            {
                Native.ReleaseDC(IntPtr.Zero, screen);
            }
        }

        public void Dispose()
        {
            if (memoryDc != IntPtr.Zero) Native.DeleteDC(memoryDc);
            if (dib != IntPtr.Zero) Native.DeleteObject(dib);
            memoryDc = dib = bits = IntPtr.Zero;
        }
    }
}
