using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace PNCPKing.App.Services;

public sealed class DesktopShortcutService
{
    private const string ShortcutFileName = "PNCP King.lnk";
    private readonly string _desktopDirectory;
    private readonly Func<string?> _executablePathProvider;

    public DesktopShortcutService()
        : this(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            () => Environment.ProcessPath)
    {
    }

    internal DesktopShortcutService(
        string desktopDirectory,
        Func<string?> executablePathProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(desktopDirectory);
        ArgumentNullException.ThrowIfNull(executablePathProvider);
        _desktopDirectory = desktopDirectory;
        _executablePathProvider = executablePathProvider;
    }

    public string ShortcutPath => Path.Combine(_desktopDirectory, ShortcutFileName);

    public void Apply(bool enabled)
    {
        if (!enabled)
        {
            if (File.Exists(ShortcutPath))
            {
                File.Delete(ShortcutPath);
            }

            return;
        }

        var executablePath = _executablePathProvider();
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new InvalidOperationException(
                "O Windows não informou o caminho do executável atual.");
        }

        executablePath = Path.GetFullPath(executablePath);
        var workingDirectory = Path.GetDirectoryName(executablePath);
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            throw new InvalidOperationException(
                "Não foi possível determinar a pasta do executável atual.");
        }

        Directory.CreateDirectory(_desktopDirectory);
        CreateOrUpdateShortcut(executablePath, workingDirectory, ShortcutPath);
    }

    private static void CreateOrUpdateShortcut(
        string executablePath,
        string workingDirectory,
        string shortcutPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Atalhos do PNCP King são compatíveis somente com o Windows.");
        }

        var shellLink = (IShellLinkW)(object)new ShellLink();
        try
        {
            Marshal.ThrowExceptionForHR(shellLink.SetPath(executablePath));
            Marshal.ThrowExceptionForHR(shellLink.SetWorkingDirectory(workingDirectory));
            Marshal.ThrowExceptionForHR(shellLink.SetDescription("Abrir o PNCP King"));
            Marshal.ThrowExceptionForHR(shellLink.SetIconLocation(executablePath, 0));
            Marshal.ThrowExceptionForHR(shellLink.SetShowCmd(1));
            ((IPersistFile)shellLink).Save(shortcutPath, true);
        }
        finally
        {
            if (Marshal.IsComObject(shellLink))
            {
                Marshal.FinalReleaseComObject(shellLink);
            }
        }
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private sealed class ShellLink
    {
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        [PreserveSig]
        int GetPath(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file,
            int maximumPath,
            IntPtr findData,
            uint flags);

        [PreserveSig]
        int GetIDList(out IntPtr itemIdList);

        [PreserveSig]
        int SetIDList(IntPtr itemIdList);

        [PreserveSig]
        int GetDescription(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name,
            int maximumName);

        [PreserveSig]
        int SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);

        [PreserveSig]
        int GetWorkingDirectory(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory,
            int maximumPath);

        [PreserveSig]
        int SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);

        [PreserveSig]
        int GetArguments(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments,
            int maximumPath);

        [PreserveSig]
        int SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);

        [PreserveSig]
        int GetHotkey(out short hotkey);

        [PreserveSig]
        int SetHotkey(short hotkey);

        [PreserveSig]
        int GetShowCmd(out int showCommand);

        [PreserveSig]
        int SetShowCmd(int showCommand);

        [PreserveSig]
        int GetIconLocation(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath,
            int maximumPath,
            out int iconIndex);

        [PreserveSig]
        int SetIconLocation(
            [MarshalAs(UnmanagedType.LPWStr)] string iconPath,
            int iconIndex);

        [PreserveSig]
        int SetRelativePath(
            [MarshalAs(UnmanagedType.LPWStr)] string path,
            uint reserved);

        [PreserveSig]
        int Resolve(IntPtr windowHandle, uint flags);

        [PreserveSig]
        int SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }
}
