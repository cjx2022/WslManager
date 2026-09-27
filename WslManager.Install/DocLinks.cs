using System;
using System.Diagnostics;

namespace WslManager.Install;

public static class DocLinks
{
	public const string Install = "https://learn.microsoft.com/en-us/windows/wsl/install";

	public const string BasicCommands = "https://learn.microsoft.com/en-us/windows/wsl/basic-commands";

	public const string ListOnline = "https://learn.microsoft.com/en-us/windows/wsl/basic-commands#list-available-linux-distributions";

	public const string InstallCmd = "https://learn.microsoft.com/en-us/windows/wsl/basic-commands#install";

	public const string CheckVersion = "https://learn.microsoft.com/en-us/windows/wsl/basic-commands#check-wsl-version";

	public const string CheckStatus = "https://learn.microsoft.com/en-us/windows/wsl/basic-commands#check-wsl-status";

	public const string SetDefaultVersion = "https://learn.microsoft.com/en-us/windows/wsl/basic-commands#set-default-wsl-version";

	public const string ImportCmd = "https://learn.microsoft.com/en-us/windows/wsl/basic-commands#import-a-distribution";

	public const string ExportCmd = "https://learn.microsoft.com/en-us/windows/wsl/basic-commands#export-a-distribution";

	public const string UnregisterCmd = "https://learn.microsoft.com/en-us/windows/wsl/basic-commands#unregister-or-uninstall-a-linux-distribution";

	public const string Troubleshooting = "https://learn.microsoft.com/en-us/windows/wsl/troubleshooting";

	public const string TroubleshootInstall = "https://learn.microsoft.com/en-us/windows/wsl/troubleshooting#installation-issues";

	public const string TroubleshootVirtualization = "https://learn.microsoft.com/en-us/windows/wsl/troubleshooting#error-0x80370102-the-virtual-machine-could-not-be-started-because-a-required-feature-is-not-installed";

	public const string ManualInstall = "https://learn.microsoft.com/en-us/windows/wsl/install-manual";

	public const string ManualStep1Wsl = "https://learn.microsoft.com/en-us/windows/wsl/install-manual#step-1---enable-the-windows-subsystem-for-linux";

	public const string ManualStep2Requirements = "https://learn.microsoft.com/en-us/windows/wsl/install-manual#step-2---check-requirements-for-running-wsl-2";

	public const string ManualStep3Vmp = "https://learn.microsoft.com/en-us/windows/wsl/install-manual#step-3---enable-virtual-machine-feature";

	public const string CustomDistro = "https://learn.microsoft.com/en-us/windows/wsl/use-custom-distro";

	public const string EnableVirtualization = "https://support.microsoft.com/windows/c5578302-6e43-4b4b-a449-8ced115f58e1";

	public const string UbuntuWslInstall = "https://ubuntu.com/wsl/docs/stable/howto/install-ubuntu-wsl2/";

	public static void Open(string url)
	{
		if (string.IsNullOrWhiteSpace(url))
		{
			return;
		}
		try
		{
			Process.Start(new ProcessStartInfo
			{
				FileName = url,
				UseShellExecute = true
			});
		}
		catch
		{
		}
	}
}
