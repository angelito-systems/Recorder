using System.Runtime.InteropServices;
using Angelito.ScreenStudio.Core;

namespace Angelito.ScreenStudio.Tests;

/// <summary>Real Windows cursors through the capture path: shapes, hotspots, animation, alpha, DPI sizes, cache.</summary>
public class CursorCaptureTests
{
    private const int Arrow = 32512, IBeam = 32513, Wait = 32514, SizeWE = 32644, Hand = 32649, AppStarting = 32650;

    [DllImport("user32.dll")] private static extern IntPtr LoadCursor(IntPtr inst, int id);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadImage(IntPtr inst, string name, uint type, int cx, int cy, uint flags);
    private static readonly string HandFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Cursors", "aero_link.cur");
    [DllImport("user32.dll")] private static extern IntPtr CreateIconIndirect(ref ICONINFO ii);
    [DllImport("user32.dll")] private static extern bool DestroyCursor(IntPtr h);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateBitmap(int w, int h, uint planes, uint bpp, byte[]? bits);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);
    [StructLayout(LayoutKind.Sequential)] private struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }

    private static (CursorShape Shape, byte[] Px) Cap(int id) => CursorCapture.Capture(LoadCursor(IntPtr.Zero, id)) ?? throw new Xunit.Sdk.XunitException($"cursor {id} not captured");
    private static int Opaque(byte[] px, int frame, CursorShape s) => Enumerable.Range(0, s.W * s.H).Count(i => px[(frame * s.W * s.H + i) * 4 + 3] > 128);

    [Theory]
    [InlineData(IBeam)]
    [InlineData(Hand)]
    [InlineData(SizeWE)]
    [InlineData(Arrow)]
    public void SystemShapesCaptureWithHotspotInside(int id)
    {
        var (s, px) = Cap(id);
        Assert.InRange(s.W, 16, 256);
        Assert.InRange(s.HotX, 0, s.W - 1);
        Assert.InRange(s.HotY, 0, s.H - 1);
        Assert.True(Opaque(px, 0, s) > 10, "cursor came out empty");
        Assert.True(Opaque(px, 0, s) < s.W * s.H * 0.9, "cursor came out as a solid block (alpha lost)");
    }

    [Fact]
    public void DistinctShapesGetDistinctIds()
    {
        var ids = new[] { Arrow, IBeam, Hand, SizeWE }.Select(i => Cap(i).Shape.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.Equal(Cap(Hand).Shape.Id, Cap(Hand).Shape.Id); // stable: same image, same id
    }

    [Theory]
    [InlineData(Wait)]
    [InlineData(AppStarting)]
    public void AnimatedCursorsKeepTheirFrames(int id)
    {
        var (s, px) = Cap(id);
        // The default Windows busy cursors are .ani; a scheme could make them static, then there is simply one frame.
        if (s.Frames == 1) return;
        Assert.True(s.Frames >= 2);
        Assert.All(s.FrameMs, ms => Assert.InRange(ms, 16, 2000));
        var first = px.AsSpan(0, s.W * s.H * 4).ToArray();
        Assert.Contains(Enumerable.Range(1, s.Frames - 1), f => !px.AsSpan(f * s.W * s.H * 4, s.W * s.H * 4).SequenceEqual(first)); // it really animates
        // Frame selection walks the cycle on the output frame grid.
        Assert.Equal(0, s.FrameAt(0, 60));
        Assert.Equal(1, s.FrameAt((s.FrameMs[0] + 1) / 1000.0, 1000));
    }

    [Fact]
    public void CustomCursorKeepsExactPixelsAndHotspot()
    {
        // 32×32: transparent except an opaque red 8×8 square at (10,12), hotspot (11,13).
        var color = new byte[32 * 32 * 4];
        for (var y = 12; y < 20; y++)
            for (var x = 10; x < 18; x++) { var i = (y * 32 + x) * 4; color[i + 2] = 255; color[i + 3] = 255; }
        var hColor = CreateBitmap(32, 32, 1, 32, color);
        var hMask = CreateBitmap(32, 32, 1, 1, new byte[32 * 32 / 8]);
        var ii = new ICONINFO { fIcon = false, xHotspot = 11, yHotspot = 13, hbmMask = hMask, hbmColor = hColor };
        var cur = CreateIconIndirect(ref ii);
        DeleteObject(hColor); DeleteObject(hMask);
        try
        {
            var (s, px) = CursorCapture.Capture(cur) ?? throw new Xunit.Sdk.XunitException("custom cursor not captured");
            Assert.Equal((32, 32, 11, 13, 1), (s.W, s.H, s.HotX, s.HotY, s.Frames));
            var inside = (15 * 32 + 12) * 4;
            Assert.Equal((0, 0, 255, 255), (px[inside], px[inside + 1], px[inside + 2], px[inside + 3]));
            Assert.Equal(0, px[(2 * 32 + 2) * 4 + 3]); // transparent stays transparent
            Assert.Equal(64, Opaque(px, 0, s));
        }
        finally { DestroyCursor(cur); }
    }

    [Theory] // larger DPI / accessibility sizes come as larger bitmaps with the hotspot scaled along
    [InlineData(32)]
    [InlineData(48)]
    [InlineData(64)]
    [InlineData(128)]
    public void SizesFollowTheLoadedCursor(int size)
    {
        var h = LoadImage(IntPtr.Zero, HandFile, 2 /* IMAGE_CURSOR */, size, size, 0x10 /* LR_LOADFROMFILE */);
        Assert.NotEqual(IntPtr.Zero, h);
        try
        {
            var (s, _) = CursorCapture.Capture(h) ?? throw new Xunit.Sdk.XunitException("not captured");
            Assert.Equal((size, size), (s.W, s.H));
            Assert.InRange(s.HotX / (double)size, 0.15, 0.55); // the hand's fingertip: same relative spot at every size
        }
        finally { DestroyCursor(h); }
    }

    [Fact]
    public void StoreCapturesOnceAndDedupes()
    {
        var dir = TempDir.Create("ass-cur").FullName;
        var store = new CursorShapeStore(dir);
        var hand = LoadCursor(IntPtr.Zero, Hand);
        var id = store.Resolve(hand);
        Assert.NotNull(id);
        Assert.Equal(id, store.Resolve(hand));
        var file = LoadImage(IntPtr.Zero, HandFile, 2, 48, 48, 0x10); // another handle
        var copy = LoadImage(IntPtr.Zero, HandFile, 2, 48, 48, 0x10); // yet another handle, same image as the previous one
        try { Assert.Equal(store.Resolve(file), store.Resolve(copy)); } finally { DestroyCursor(copy); DestroyCursor(file); }
        Assert.Equal(2, Directory.GetFiles(Path.Combine(dir, CursorShapeStore.Folder), "*.bgra").Length); // system hand + 48 px hand, stored once each
        var loaded = CursorShapeStore.Load(dir);
        Assert.Equal(store.Shapes[id!], loaded[id!] with { FrameMs = store.Shapes[id!].FrameMs });
        Assert.True(store.Resolve(LoadCursor(IntPtr.Zero, Arrow)) is { } a && store.Shapes[a].Arrow);
    }
}
