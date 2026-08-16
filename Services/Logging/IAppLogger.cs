namespace SwitchBoard.Services.Logging;

public interface IAppLogger
{
    void Info(string area, string message);
    void Warning(string area, string message);
    void Error(string area, Exception exception, string? message = null);
}
