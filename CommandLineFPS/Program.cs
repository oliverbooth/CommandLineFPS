using System;
using System.Runtime.InteropServices;

namespace CommandLineFPS;

internal static class Program
{
    private struct COORD
    {
        public short X;
        public short Y;
    }

    private const long GenericRead = 0x80000000L;
    private const long GenericWrite = 0x40000000L;

    [DllImport("msvcrt.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe int swprintf_s(char* buffer, int bufferCount, [MarshalAs(UnmanagedType.LPWStr)] string format,
        float f0, float f1, float f2, float f3);

    [DllImport("kernel32.dll")]
    private static extern IntPtr CreateConsoleScreenBuffer(long dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwFlags, IntPtr lpScreenBufferData);

    [DllImport("kernel32.dll")]
    private static extern bool SetConsoleActiveScreenBuffer(IntPtr hConsoleOuptut);

    [DllImport("kernel32.dll")]
    private static extern unsafe bool WriteConsoleOutputCharacterW(IntPtr hConsoleOutput, char* lpCharacter, uint length,
        COORD dwWriteCoord, ref uint lpNumberOfCharsWritten);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(ushort vKey);

    private const int ScreenWidth = 120; // Console Screen Size X (columns)
    private const int ScreenHeight = 40; // Console Screen Size Y (rows)

    private const int MapWidth = 16; // World Dimensions
    private const int MapHeight = 16;

    private const float Fov = MathF.PI / 4.0f; // Field of View
    private const float Depth = 16.0f;         // Maximum rendering distance
    private static float s_playerAngle = 0.0f; // Player Start Rotation
    private static float s_playerX = 14.7f;    // Player Start X
    private static float s_playerY = 5.09f;    // Player Start Y
    private static float s_speed = 5.0f;       // Walking Speed

    private static unsafe void Main()
    {
        char* screen = stackalloc char[ScreenWidth * ScreenHeight];
        IntPtr hConsole = CreateConsoleScreenBuffer(GenericRead | GenericWrite, 0, IntPtr.Zero, 1, IntPtr.Zero);
        SetConsoleActiveScreenBuffer(hConsole);

        uint bytesWritten = 0;

        // // Create Map of world space # = wall block, . = space
        var map = string.Empty;
        map += "#########.......";
        map += "#...............";
        map += "#.......########";
        map += "#..............#";
        map += "#......##......#";
        map += "#......##......#";
        map += "#..............#";
        map += "###............#";
        map += "##.............#";
        map += "#......####..###";
        map += "#......#.......#";
        map += "#......#.......#";
        map += "#..............#";
        map += "#......#########";
        map += "#..............#";
        map += "################";

        DateTime tp1 = DateTime.Now;
        DateTime tp2 = DateTime.Now;

        Span<(float, float)> p = stackalloc (float, float)[4];

        while (true)
        {
            // We'll need time differential per frame to calculate modification
            // to movement speeds, to ensure consistant movement, as ray-tracing
            // is non-deterministic
            tp2 = DateTime.Now;
            TimeSpan elapsedTime = tp2 - tp1;
            tp1 = tp2;
            var elapsedSeconds = (float) elapsedTime.TotalSeconds;

            // Handle CCW Rotation
            if ((GetAsyncKeyState('A') & 0x8000) != 0)
                s_playerAngle -= (s_speed * 0.75f) * elapsedSeconds;

            // Handle CW Rotation
            if ((GetAsyncKeyState('D') & 0x8000) != 0)
                s_playerAngle += (s_speed * 0.75f) * elapsedSeconds;

            // Handle Forwards movement & collision
            if ((GetAsyncKeyState('W') & 0x8000) != 0)
            {
                s_playerX += MathF.Sin(s_playerAngle) * s_speed * elapsedSeconds;
                s_playerY += MathF.Cos(s_playerAngle) * s_speed * elapsedSeconds;
                if (map[(int) s_playerX * MapWidth + (int) s_playerY] == '#')
                {
                    s_playerX -= MathF.Sin(s_playerAngle) * s_speed * elapsedSeconds;
                    s_playerY -= MathF.Cos(s_playerAngle) * s_speed * elapsedSeconds;
                }
            }

            // Handle backwards movement & collision
            if ((GetAsyncKeyState('S') & 0x8000) != 0)
            {
                s_playerX -= MathF.Sin(s_playerAngle) * s_speed * elapsedSeconds;
                s_playerY -= MathF.Cos(s_playerAngle) * s_speed * elapsedSeconds;
                if (map[(int) s_playerX * MapWidth + (int) s_playerY] == '#')
                {
                    s_playerX += MathF.Sin(s_playerAngle) * s_speed * elapsedSeconds;
                    s_playerY += MathF.Cos(s_playerAngle) * s_speed * elapsedSeconds;
                }
            }

            for (var x = 0; x < ScreenWidth; x++)
            {
                // For each column, calculate the projected ray angle into world space
                float rayAngle = (s_playerAngle - Fov / 2.0f) + (x / (float) ScreenWidth) * Fov;

                // Find distance to wall
                var stepSize = 0.1f;       // Increment size for ray casting, decrease to increase										
                var distanceToWall = 0.0f; //                                      resolution

                var hitWall = false;  // Set when ray hits wall block
                var boundary = false; // Set when ray hits boundary between two wall blocks

                float eyeX = MathF.Sin(rayAngle); // Unit vector for ray in player space
                float eyeY = MathF.Cos(rayAngle);

                // Incrementally cast ray from player, along ray angle, testing for 
                // intersection with a block
                while (!hitWall && distanceToWall < Depth)
                {
                    distanceToWall += stepSize;
                    var testX = (int) (s_playerX + eyeX * distanceToWall);
                    var testY = (int) (s_playerY + eyeY * distanceToWall);

                    // Test if ray is out of bounds
                    if (testX < 0 || testX >= MapWidth || testY < 0 || testY >= MapHeight)
                    {
                        hitWall = true; // Just set distance to maximum depth
                        distanceToWall = Depth;
                    }
                    else
                    {
                        // Ray is inbounds so test to see if the ray cell is a wall block
                        if (map[testX * MapWidth + testY] == '#')
                        {
                            // Ray has hit wall
                            hitWall = true;

                            // To highlight tile boundaries, cast a ray from each corner
                            // of the tile, to the player. The more coincident this ray
                            // is to the rendering ray, the closer we are to a tile 
                            // boundary, which we'll shade to add detail to the walls
                            for (int tx = 0; tx < 2; tx++)
                            for (int ty = 0; ty < 2; ty++)
                            {
                                // Angle of corner to eye
                                float vy = (float) testY + ty - s_playerY;
                                float vx = (float) testX + tx - s_playerX;
                                float d = MathF.Sqrt(vx * vx + vy * vy);
                                float dot = (eyeX * vx / d) + (eyeY * vy / d);
                                p[tx + (ty * 2)] = (d, dot);
                            }

                            // Sort Pairs from closest to farthest
                            p.Sort((left, right) => left.Item1 < right.Item1 ? -1 : 1);

                            // First two/three are closest (we will never see all four)
                            float bound = 0.01f;
                            if (MathF.Acos(p[0].Item2) < bound) boundary = true;
                            if (MathF.Acos(p[1].Item2) < bound) boundary = true;
                            if (MathF.Acos(p[2].Item2) < bound) boundary = true;
                        }
                    }
                }

                // Calculate distance to ceiling and floor
                var ceiling = (int) (ScreenHeight / 2.0f - ScreenHeight / distanceToWall);
                int floor = ScreenHeight - ceiling;

                // Shader walls based on distance
                char shade;
                if (distanceToWall <= Depth / 4.0f) shade = '█';     // very close
                else if (distanceToWall < Depth / 3.0f) shade = '▓'; //
                else if (distanceToWall < Depth / 2.0f) shade = '▒'; //
                else if (distanceToWall < Depth) shade = '░';        //
                else shade = ' ';                                    // Too far away

                if (boundary) shade = ' '; // Black it out

                for (int y = 0; y < ScreenHeight; y++)
                {
                    // Each Row
                    if (y <= ceiling)
                        screen[y * ScreenWidth + x] = ' ';
                    else if (y > ceiling && y < floor)
                        screen[y * ScreenWidth + x] = shade;
                    else // Floor
                    {
                        // Shade floor based on distance
                        float b = 1.0f - ((y - ScreenHeight / 2.0f) / (ScreenHeight / 2.0f));
                        if (b < 0.25) shade = '#';
                        else if (b < 0.5) shade = 'x';
                        else if (b < 0.75) shade = '.';
                        else if (b < 0.9) shade = '-';
                        else shade = ' ';
                        screen[y * ScreenWidth + x] = shade;
                    }
                }
            }

            // Display Stats
            swprintf_s(screen, 40, "X=%3.2f, Y=%3.2f, A=%3.2f FPS=%3.2f", s_playerX, s_playerY, s_playerAngle,
                1.0f / elapsedSeconds);

            // Display Map
            for (var nx = 0; nx < MapWidth; nx++)
            for (var ny = 0; ny < MapHeight; ny++)
                screen[(ny + 1) * ScreenWidth + nx] = map[ny * MapWidth + nx];
            screen[((int) s_playerX + 1) * ScreenWidth + (int) s_playerY] = 'P';

            // Display Frame
            screen[ScreenWidth * ScreenHeight - 1] = '\0';
            WriteConsoleOutputCharacterW(hConsole, screen, ScreenWidth * ScreenHeight, new COORD(), ref bytesWritten);
        }
    }
}
