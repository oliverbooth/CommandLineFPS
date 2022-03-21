using System;
using System.Threading;
using PInvoke;

namespace CommandLineFPS;

internal static class Program
{
    private const int ScreenWidth = 120; // Console Screen Size X (columns)
    private const int ScreenHeight = 40; // Console Screen Size Y (rows)

    private const int MapWidth = 16; // World Dimensions
    private const int MapHeight = 16;

    private const float Fov = MathF.PI / 4.0f; // Field of View
    private const float Depth = 16.0f;         // Maximum rendering distance
    private static float s_playerAngle;        // Player Start Rotation
    private static float s_playerX = 14.7f;    // Player Start X
    private static float s_playerY = 5.09f;    // Player Start Y
    private const float Speed = 5.0f;          // Walking Speed

    private static volatile bool s_moveForward; // Movement flags
    private static volatile bool s_moveBack;
    private static volatile bool s_turnLeft;
    private static volatile bool s_turnRight;

    private static unsafe void Main()
    {
        var writeCoord = new COORD();
        char* screen = stackalloc char[ScreenWidth * ScreenHeight];

        var access = new Kernel32.ACCESS_MASK((uint) (Native.GenericRead | Native.GenericWrite));
        const Kernel32.ConsoleScreenBufferFlag flags = Kernel32.ConsoleScreenBufferFlag.CONSOLE_TEXTMODE_BUFFER;
        var securityAttributes = Kernel32.SECURITY_ATTRIBUTES.Create();
        IntPtr hConsole =
            Kernel32.CreateConsoleScreenBuffer(access, Kernel32.FileShare.None, securityAttributes, flags, IntPtr.Zero);
        Kernel32.SetConsoleActiveScreenBuffer(hConsole);

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

        fixed (char* statsFormat = "X=%3.2f, Y=%3.2f, A=%3.2f FPS=%3.2f ")
        fixed (char* mapPtr = map)
        {
            int tp1 = Environment.TickCount;
            Span<(float, float)> p = stackalloc (float, float)[4];

            new Thread(InputWorker).Start();

            GC.TryStartNoGCRegion(1024 * 1024 * 100);
            GC.Collect();

            while (true)
            {
                // We'll need time differential per frame to calculate modification
                // to movement speeds, to ensure consistent movement, as ray-tracing
                // is non-deterministic
                int tp2 = Environment.TickCount;
                int elapsedTime = tp2 - tp1;
                tp1 = tp2;
                float elapsedSeconds = elapsedTime / 1000.0f;

                float movementFactor = Speed * elapsedSeconds;
                float xDelta = MathF.Sin(s_playerAngle) * movementFactor;
                float yDelta = MathF.Cos(s_playerAngle) * movementFactor;
                float angleDelta = (Speed * 0.75f) * elapsedSeconds;

                // Handle CCW Rotation
                if (s_turnLeft)
                    s_playerAngle -= angleDelta;

                // Handle CW Rotation
                if (s_turnRight)
                    s_playerAngle += angleDelta;


                // Handle Forwards movement & collision
                if (s_moveForward)
                {
                    s_playerX += xDelta;
                    s_playerY += yDelta;
                    if (mapPtr[(int) s_playerX * MapWidth + (int) s_playerY] == '#')
                    {
                        s_playerX -= xDelta;
                        s_playerY -= yDelta;
                    }
                }

                // Handle backwards movement & collision
                if (s_moveBack)
                {
                    s_playerX -= xDelta;
                    s_playerY -= yDelta;
                    if (mapPtr[(int) s_playerX * MapWidth + (int) s_playerY] == '#')
                    {
                        s_playerX += xDelta;
                        s_playerY += yDelta;
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
                            if (mapPtr[testX * MapWidth + testY] == '#')
                            {
                                // Ray has hit wall
                                hitWall = true;

                                // Reset tuple span
                                for (int i = 0; i < p.Length; i++)
                                    p[i] = (float.MaxValue, 0);

                                // To highlight tile boundaries, cast a ray from each corner
                                // of the tile, to the player. The more coincident this ray
                                // is to the rendering ray, the closer we are to a tile 
                                // boundary, which we'll shade to add detail to the walls
                                for (int tx = 0; tx < 2; tx++)
                                for (int ty = 0; ty < 2; ty++)
                                {
                                    // Angle of corner to eye
                                    float vy = testY + ty - s_playerY;
                                    float vx = testX + tx - s_playerX;
                                    float d = MathF.Sqrt(vx * vx + vy * vy);
                                    float dot = (eyeX * vx / d) + (eyeY * vy / d);
                                    p[tx + (ty * 2)] = (d, dot);

                                    // My attempt at some kind of hacky insertion sort
                                    // Inserts tuple closest to farthest
                                    for (int i = 0; i < p.Length; i++)
                                    {
                                        if (p[i].Item1 > d)
                                        {
                                            p[i] = (d, dot);
                                            break;
                                        }
                                    }
                                }

                                // First two/three are closest (we will never see all four)
                                const float bound = 0.01f;
                                if (MathF.Acos(p[0].Item2) < bound ||
                                    MathF.Acos(p[1].Item2) < bound ||
                                    MathF.Acos(p[2].Item2) < bound)
                                    boundary = true;
                            }
                        }
                    }

                    // Calculate distance to ceiling and floor
                    var ceiling = (int) (ScreenHeight / 2.0f - ScreenHeight / distanceToWall);
                    int floor = ScreenHeight - ceiling;

                    // Shader walls based on distance
                    char shade;
                    if (distanceToWall <= Depth / 4.0f) shade = '█';     // Very close
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
                Native.swprintf_s(screen, 40, statsFormat, s_playerX, s_playerY, s_playerAngle, 1.0f / elapsedSeconds);

                // Display Map
                for (var nx = 0; nx < MapWidth; nx++)
                for (var ny = 0; ny < MapHeight; ny++)
                    screen[((ny + 1) * ScreenWidth + nx) + 2] = mapPtr[ny * MapWidth + nx];
                screen[(((int) s_playerX + 1) * ScreenWidth + (int) s_playerY) + 2] = 'P';

                // Display Frame
                screen[ScreenWidth * ScreenHeight - 1] = '\0';
                Native.WriteConsoleOutputCharacterW(hConsole, screen, ScreenWidth * ScreenHeight, writeCoord, ref bytesWritten);
            }
        }

        // ReSharper disable once FunctionNeverReturns
    }

    private static void InputWorker()
    {
        while (true)
        {
            s_moveForward = (User32.GetAsyncKeyState('W') & 0x8000) != 0;
            s_moveBack = (User32.GetAsyncKeyState('S') & 0x8000) != 0;
            s_turnLeft = (User32.GetAsyncKeyState('A') & 0x8000) != 0;
            s_turnRight = (User32.GetAsyncKeyState('D') & 0x8000) != 0;
        }

        // ReSharper disable once FunctionNeverReturns
    }
}
