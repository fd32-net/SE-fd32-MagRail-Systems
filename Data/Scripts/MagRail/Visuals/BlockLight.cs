using VRage.Game.ModAPI;
using VRageMath;

namespace MagRail
{
    // A block's status light: the model's 'Emissive' material (the glow round the glyph plates of the Train Control
    // Unit and the Autopilot), lit like a vanilla block's. The same colours on both blocks: idle yellow, driving
    // blue, arrived green, error red (blinking), off red, not built or damaged dark. Painted only on a change and
    // again now and then: a build stage model swap resets the material. Clients only (a light is a look).
    public class BlockLight
    {
        public const int BlinkTicks = 30;
        const int RepaintTicks = 120;
        public static readonly Color Idle = Color.Yellow, Driving = new Color(0, 160, 255), Arrived = Color.Green, Error = Color.Red;

        Color color;
        float emissivity = -1;
        int paintTick = -999;

        // Blink phase: false every other BlinkTicks.
        public static bool BlinkOn(int tick)
        {
            return (tick / BlinkTicks) % 2 == 0;
        }

        // Dark, red or null: the light for a block that is not built, damaged or off; null = it works.
        public static Color? NotWorking(IMyCubeBlock block)
        {
            if (!block.IsFunctional)
                return Color.Black;
            if (!block.IsWorking)
                return Error;
            return null;
        }

        public void Paint(IMyCubeBlock block, Color c, bool on, int tick)
        {
            float e = on && c != Color.Black ? 1 : 0;
            if (c == color && e == emissivity && tick - paintTick < RepaintTicks && tick >= paintTick)
                return;
            color = c;
            emissivity = e;
            paintTick = tick;
            block.SetEmissiveParts("Emissive", c, e);
        }
    }
}
