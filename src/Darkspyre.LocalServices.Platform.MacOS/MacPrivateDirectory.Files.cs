using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Darkspyre.LocalServices.Platform.MacOS;

public sealed partial class MacPrivateDirectory
{
    internal byte[]? ReadPrivateFile(string name)
    {
        RequireChildName(name);
        int fd = MacNative.OpenAt(this.handle, name, MacNative.NoFollow | MacNative.CloseOnExec);
        if (fd < 0)
        {
            int error = Marshal.GetLastPInvokeError();
            if (error == MacNative.Missing) return null;
            throw MacNative.Error("read discovery file", error);
        }
        using var file = new SafeFileHandle((nint)fd, ownsHandle: true);
        MacNative.InspectPrivate(file, directory: false);
        long length = RandomAccess.GetLength(file);
        if (length is <= 0 or > 4096) throw new InvalidDataException("A discovery file is outside its size limit.");
        byte[] bytes = new byte[(int)length];
        int offset = 0;
        while (offset < bytes.Length)
        {
            int read = RandomAccess.Read(file, bytes.AsSpan(offset), offset);
            if (read == 0) throw new EndOfStreamException("The discovery file is truncated.");
            offset += read;
        }
        if (RandomAccess.GetLength(file) != length) throw new IOException("The discovery file changed while being read.");
        return bytes;
    }

    /// <summary>The family owner publishes only after acquiring ownership. Readers see an entire old or new file.</summary>
    internal void PublishPrivateFile(string name, ReadOnlySpan<byte> bytes)
    {
        RequireChildName(name);
        if (bytes.Length is 0 or > 4096) throw new InvalidDataException("A discovery file is outside its size limit.");
        byte[] template = Encoding.UTF8.GetBytes(".publish-XXXXXX\0");
        int fd = MacNative.CreateTemporaryAt(this.handle, template, 0, MacNative.CloseOnExec);
        if (fd < 0) throw MacNative.Error("create discovery file", Marshal.GetLastPInvokeError());
        using var file = new SafeFileHandle((nint)fd, ownsHandle: true);
        string temporaryName = Encoding.UTF8.GetString(template.AsSpan(0, Array.IndexOf(template, (byte)0)));
        bool published = false;
        Exception? failure = null;
        try
        {
            MacNative.InspectPrivate(file, directory: false);
            RandomAccess.Write(file, bytes, 0);
            RandomAccess.FlushToDisk(file);
            if (MacNative.RenameAt(this.handle, temporaryName, this.handle, name, 0) != 0)
                throw MacNative.Error("publish discovery file", Marshal.GetLastPInvokeError());
            published = true;
        }
        catch (Exception error) { failure = error; }
        if (!published && MacNative.UnlinkAt(this.handle, temporaryName, 0) != 0)
        {
            IOException cleanup = MacNative.Error("remove discovery temporary file", Marshal.GetLastPInvokeError());
            if (failure is not null) throw new AggregateException(failure, cleanup);
            throw cleanup;
        }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    internal void RemoveChild(string name)
    {
        RequireChildName(name);
        if (MacNative.UnlinkAt(this.handle, name, 0) != 0)
        {
            int error = Marshal.GetLastPInvokeError();
            if (error != MacNative.Missing) throw MacNative.Error("remove owned endpoint file", error);
        }
    }
}
