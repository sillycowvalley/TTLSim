using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TTLSim.UI.View;

public sealed partial class SchematicCanvas
{
    // ---------------------------------------------------------------- WM_GESTURE pan support

    private const int WM_GESTURE = 0x0119;
    private const ulong GID_PAN  = 4;

    // GF_BEGIN = 0x01 on the first gesture message in a sequence
    private const ulong GF_BEGIN = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    private struct GESTUREINFO
    {
        public uint   cbSize;
        public uint   dwFlags;
        public ulong  dwID;
        public IntPtr hwndTarget;
        public POINTS ptsLocation;
        public uint   dwInstanceID;
        public uint   dwSequenceID;
        public ulong  ullArguments;
        public uint   cbExtraArgs;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTS { public short x; public short y; }

    [DllImport("user32.dll")]
    private static extern bool GetGestureInfo(IntPtr hGestureInfo, ref GESTUREINFO pGestureInfo);

    [DllImport("user32.dll")]
    private static extern bool CloseGestureInfoHandle(IntPtr hGestureInfo);

    // Screen position of the previous GID_PAN message (used to compute delta).
    private Point gesturePanLast;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_GESTURE)
        {
            var gi = new GESTUREINFO { cbSize = (uint)Marshal.SizeOf<GESTUREINFO>() };
            if (GetGestureInfo(m.LParam, ref gi) && gi.dwID == GID_PAN)
            {
                // ptsLocation is in screen pixels; convert to client coords.
                var screenPt = new Point(gi.ptsLocation.x, gi.ptsLocation.y);
                var clientPt = PointToClient(screenPt);

                if ((gi.dwFlags & GF_BEGIN) != 0)
                {
                    // First message in this pan sequence — just record position.
                    gesturePanLast = clientPt;
                }
                else
                {
                    int dx = clientPt.X - gesturePanLast.X;
                    int dy = clientPt.Y - gesturePanLast.Y;
                    gesturePanLast = clientPt;

                    PanOffset = new PointF(PanOffset.X + dx, PanOffset.Y + dy);
                    Invalidate();
                    ViewChanged?.Invoke(this, EventArgs.Empty);
                }

                CloseGestureInfoHandle(m.LParam);
                m.Result = IntPtr.Zero;   // mark handled
                return;
            }
            // Non-PAN gesture (e.g. GID_ZOOM): do NOT close the handle;
            // let base.WndProc convert it to OnMouseWheel as normal.
        }

        base.WndProc(ref m);
    }
}
