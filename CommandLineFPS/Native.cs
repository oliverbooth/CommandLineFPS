using System;
using System.Runtime.InteropServices;
using PInvoke;

namespace CommandLineFPS;

internal static class Native
{
    public const long GenericRead = 0x80000000L;
    public const long GenericWrite = 0x40000000L;

    [DllImport("msvcrt.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern unsafe int swprintf_s(char* buffer, int bufferCount, char* format, float f0, float f1, float f2,
        float f3);

    [DllImport("kernel32.dll")]
    public static extern unsafe bool WriteConsoleOutputCharacterW(IntPtr hConsoleOutput, char* lpCharacter, uint length,
        COORD dwWriteCoord, ref uint lpNumberOfCharsWritten);
}
