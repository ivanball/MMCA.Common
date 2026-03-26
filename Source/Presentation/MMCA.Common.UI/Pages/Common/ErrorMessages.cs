namespace MMCA.Common.UI.Pages.Common;

/// <summary>
/// Consistent user-facing notification messages used by all page code-behinds.
/// Centralizes message formatting so snackbar messages are uniform across the application.
/// </summary>
public static class ErrorMessages
{
    public static string LoadError(string entityName, Exception ex) =>
        $"Error loading {entityName}. {ex.Message}";

    public static string SaveError(string entityName, Exception ex) =>
        $"Error saving {entityName}. {ex.Message}";

    public static string DeleteError(string entityName, Exception ex) =>
        $"Error deleting {entityName}. {ex.Message}";

    public static string DeleteFailed(string entityName) =>
        $"Failed to delete the {entityName}.";

    public static string NotFound(string entityName, object id) =>
        $"{entityName} with Id {id} was not found.";

    public static string ValidationError =>
        "There were validation errors. Please check the form.";

    public static string Success(string entityName, string action) =>
        $"{entityName} {action} successfully.";
}
