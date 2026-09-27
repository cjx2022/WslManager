namespace WslManager.Wsl;

public class WslResult
{
	public int ExitCode { get; set; }

	public string StdOut { get; set; }

	public string StdErr { get; set; }

	public bool Success { get; set; }

	public string ErrorMessage { get; set; }

	public bool Graceful { get; set; }

	public bool Forced { get; set; }

	public WslResult()
	{
		ExitCode = -1;
		StdOut = "";
		StdErr = "";
		Success = false;
		ErrorMessage = "";
		Graceful = false;
		Forced = false;
	}
}
