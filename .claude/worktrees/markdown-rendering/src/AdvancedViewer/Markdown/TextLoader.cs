using System;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;

namespace AdvancedViewer.Markdown;

/// <summary>
/// Reads a whole text file into memory with ReadFile (the Win32 half of text loading; decoding
/// bytes to a string is Markdown/TextDecoder.cs). No memory mapping: an I/O error on a mapped
/// view raises EXCEPTION_IN_PAGE_ERROR, which managed code cannot catch under Native AOT, while
/// ReadFile just fails. Reads at most <see cref="MaxBytes"/>; longer files are cut and flagged.
/// The handle is opened with full sharing and closed before returning.
/// </summary>
internal static unsafe class TextLoader
{
    /// <summary>Read cap: 32 MB.</summary>
    public const int MaxBytes = 32 * 1024 * 1024;

    private const int Chunk = 4 * 1024 * 1024;

    /// <summary>
    /// Returns the file's bytes (up to <see cref="MaxBytes"/>; an empty array for a 0-byte file),
    /// or null with the reason in <paramref name="error"/>. <paramref name="truncated"/> is set when
    /// the file is longer than the cap; <paramref name="fileSize"/> is the full size on disk.
    /// </summary>
    public static byte[]? Read(string path, out bool truncated, out long fileSize, out string? error)
    {
        truncated = false;
        fileSize = 0;
        error = null;
        string native = MappedFile.ToNativePath(path);
        HANDLE file;
        fixed (char* p = native)
        {
            file = PInvoke.CreateFile(
                p,
                0x80000000u /* GENERIC_READ */,
                FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE | FILE_SHARE_MODE.FILE_SHARE_DELETE,
                null,
                FILE_CREATION_DISPOSITION.OPEN_EXISTING,
                FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_SEQUENTIAL_SCAN,
                HANDLE.Null);
        }
        if (file == (HANDLE)(nint)(-1) || file == HANDLE.Null)
        {
            error = "CreateFileW failed, Win32 error " + Marshal.GetLastPInvokeError();
            return null;
        }

        try
        {
            if (PInvoke.GetFileType(file) != FILE_TYPE.FILE_TYPE_DISK)
            {
                error = "not a disk file";
                return null;
            }

            long size;
            if (!PInvoke.GetFileSizeEx(file, &size))
            {
                error = "GetFileSizeEx failed, Win32 error " + Marshal.GetLastPInvokeError();
                return null;
            }
            fileSize = size;
            if (size == 0) return Array.Empty<byte>();

            int want = (int)Math.Min(size, MaxBytes);
            truncated = size > MaxBytes;
            byte[] buffer = GC.AllocateUninitializedArray<byte>(want);
            int total = 0;
            fixed (byte* b = buffer)
            {
                while (total < want)
                {
                    uint read;
                    uint ask = (uint)Math.Min(Chunk, want - total);
                    if (!PInvoke.ReadFile(file, b + total, ask, &read, null))
                    {
                        error = "ReadFile failed, Win32 error " + Marshal.GetLastPInvokeError();
                        return null;
                    }
                    if (read == 0) break; // file shrank since GetFileSizeEx
                    total += (int)read;
                }
            }
            if (total < want) Array.Resize(ref buffer, total);
            return buffer;
        }
        finally
        {
            PInvoke.CloseHandle(file);
        }
    }
}
