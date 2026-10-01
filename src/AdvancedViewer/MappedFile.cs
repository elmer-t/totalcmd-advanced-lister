using System;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;
using Windows.Win32.System.Memory;

namespace AdvancedViewer;

/// <summary>
/// Read-only memory-mapped view of a whole file. The file and mapping handles are closed
/// right after MapViewOfFile: the view keeps the section alive, and holding no file handle
/// (plus FILE_SHARE_DELETE on open) keeps rename/delete of the shown file possible.
/// A 0-byte file gets Size=0 and no mapping (CreateFileMapping fails on empty files).
/// On x64 the whole file is mapped: mapping only reserves address space, so a 4.5 GB file
/// costs no more than a 1 KB one (pages fault in when the visible rows are read).
/// </summary>
internal sealed unsafe class MappedFile : IDisposable
{
    public byte* Data { get; private set; }
    public long Size { get; }

    private MappedFile(byte* data, long size)
    {
        Data = data;
        Size = size;
    }

    /// <summary>Returns null (reason in <paramref name="error"/>) if the file cannot be opened or mapped.</summary>
    public static MappedFile? Open(string path, out string? error)
    {
        error = null;
        string native = ToNativePath(path);
        HANDLE file;
        fixed (char* p = native)
        {
            file = PInvoke.CreateFile(
                p,
                0x80000000u /* GENERIC_READ */,
                FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE | FILE_SHARE_MODE.FILE_SHARE_DELETE,
                null,
                FILE_CREATION_DISPOSITION.OPEN_EXISTING,
                FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_NORMAL,
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
            if (size == 0)
                return new MappedFile(null, 0);

            HANDLE mapping = PInvoke.CreateFileMapping(file, null, PAGE_PROTECTION_FLAGS.PAGE_READONLY, 0, 0, null);
            if (mapping == HANDLE.Null)
            {
                error = "CreateFileMappingW failed, Win32 error " + Marshal.GetLastPInvokeError();
                return null;
            }
            try
            {
                MEMORY_MAPPED_VIEW_ADDRESS view = PInvoke.MapViewOfFile(mapping, FILE_MAP.FILE_MAP_READ, 0, 0, 0);
                if (view.Value == null)
                {
                    error = "MapViewOfFile failed, Win32 error " + Marshal.GetLastPInvokeError();
                    return null;
                }
                return new MappedFile((byte*)view.Value, size);
            }
            finally
            {
                PInvoke.CloseHandle(mapping);
            }
        }
        finally
        {
            PInvoke.CloseHandle(file);
        }
    }

    /// <summary>Prefix paths of MAX_PATH or more with \\?\ so CreateFileW accepts them in a host that is not longPathAware.</summary>
    private static string ToNativePath(string path)
    {
        if (path.Length < 260 || path.StartsWith(@"\\?\", StringComparison.Ordinal))
            return path;
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
            return @"\\?\UNC\" + path.Substring(2);
        return @"\\?\" + path;
    }

    public void Dispose()
    {
        if (Data != null)
        {
            PInvoke.UnmapViewOfFile(new MEMORY_MAPPED_VIEW_ADDRESS(Data));
            Data = null;
        }
    }
}
