using System.Runtime.InteropServices;
using System.Text;

namespace PNCPKing.App.Services;

internal static class DiagnosticMail
{
    internal const string Recipient = "felipearcencio@gmail.com";
    private const string Body = "Segue o registro do erro ocorrido no PNCP King.";
    internal enum Outcome { Submitted, Canceled, Draft }
    internal sealed record Result(Outcome Status, string ReportPath, string? DraftPath = null);

    // Called only after the user authorizes this particular incident.
    internal static Result Submit(string directory, string incident, Version version, nint owner,
        Func<nint, string, string, uint>? send = null)
    {
        var report = Path.Combine(directory, $"pncpking-erro-{Guid.NewGuid():N}.txt");
        File.WriteAllText(report, $"PNCP King {version}{Environment.NewLine}{incident}", Encoding.UTF8);
        var subject = $"PNCP King {version.ToString(3)} - diagnostico de erro";
        uint status;
        try { status = (send ?? SendWithWindowsMail)(owner, subject, report); }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            status = 2; // No compatible Simple MAPI provider.
        }
        if (status == 0) return new(Outcome.Submitted, report);
        if (status == 1) return new(Outcome.Canceled, report); // MAPI_E_USER_ABORT

        // An unsent draft works even when the provider cannot submit mail directly.
        var draft = Path.ChangeExtension(report, ".eml");
        var boundary = $"pncp-{Guid.NewGuid():N}";
        var attachment = Convert.ToBase64String(File.ReadAllBytes(report), Base64FormattingOptions.InsertLineBreaks);
        var lines = new[]
        {
            "X-Unsent: 1", $"To: {Recipient}", $"Subject: {subject}", "MIME-Version: 1.0",
            $"Content-Type: multipart/mixed; boundary=\"{boundary}\"", "", $"--{boundary}",
            "Content-Type: text/plain; charset=UTF-8", "Content-Transfer-Encoding: base64", "",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(Body)), $"--{boundary}",
            "Content-Type: text/plain; charset=UTF-8",
            $"Content-Disposition: attachment; filename=\"{Path.GetFileName(report)}\"",
            "Content-Transfer-Encoding: base64", "", attachment, $"--{boundary}--", ""
        };
        File.WriteAllText(draft, string.Join("\r\n", lines), Encoding.ASCII);
        return new(Outcome.Draft, report, draft);
    }

    private static uint SendWithWindowsMail(nint owner, string subject, string path)
    {
        using var to = new NativeDescriptor<MapiRecipient>(new()
        {
            RecipientClass = 1, Name = Recipient, Address = "SMTP:" + Recipient
        });
        using var attachment = new NativeDescriptor<MapiAttachment>(new()
        {
            Position = uint.MaxValue, Path = path, Name = System.IO.Path.GetFileName(path)
        });
        var message = new MapiMessage
        {
            Subject = subject, Note = Body, RecipientCount = 1, Recipients = to.Pointer,
            FileCount = 1, Files = attachment.Pointer
        };
        // The configured mail client owns the account, credentials and outbox.
        return MAPISendMailW(0, owner, ref message, 0, 0);
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("mapi32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint MAPISendMailW(nuint session, nint owner, ref MapiMessage message, uint flags, uint reserved);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MapiMessage
    {
        public uint Reserved;
        public string? Subject, Note, MessageType, DateReceived, ConversationId;
        public uint Flags;
        public nint Originator;
        public uint RecipientCount;
        public nint Recipients;
        public uint FileCount;
        public nint Files;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MapiRecipient
    {
        public uint Reserved, RecipientClass;
        public string? Name, Address;
        public uint EntryIdSize;
        public nint EntryId;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MapiAttachment
    {
        public uint Reserved, Flags, Position;
        public string? Path, Name;
        public nint FileType;
    }

    private sealed class NativeDescriptor<T> : IDisposable where T : struct
    {
        public nint Pointer { get; } = Marshal.AllocHGlobal(Marshal.SizeOf<T>());
        public NativeDescriptor(T value)
        {
            try { Marshal.StructureToPtr(value, Pointer, false); }
            catch { Marshal.FreeHGlobal(Pointer); throw; }
        }
        public void Dispose()
        {
            Marshal.DestroyStructure<T>(Pointer);
            Marshal.FreeHGlobal(Pointer);
        }
    }
}
