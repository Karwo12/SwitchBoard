namespace SwitchBoard.Services;

public interface IUserDialogService
{
    bool Confirm(string title, string message);
}
