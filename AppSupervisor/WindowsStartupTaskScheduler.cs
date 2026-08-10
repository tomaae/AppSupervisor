using System.Runtime.InteropServices;

namespace AppSupervisor;

/// <summary>
/// Reads and writes AppSupervisor's elevated current-user logon task through the Windows Task Scheduler COM API.
/// </summary>
internal sealed class WindowsStartupTaskScheduler
{
    private const string SchedulerProgId = "Schedule.Service";
    private const string RootFolderPath = "\\";
    private const int ErrorFileNotFoundHResult = unchecked((int)0x80070002);
    private const int TaskActionExecute = 0;
    private const int TaskCreateOrUpdate = 6;
    private const int TaskInstancesIgnoreNew = 2;
    private const int TaskLogonInteractiveToken = 3;
    private const int TaskRunLevelHighest = 1;
    private const int TaskTriggerLogon = 9;

    private readonly string _taskName;

    /// <summary>
    /// Creates a Task Scheduler adapter for one named task in the scheduler root folder.
    /// </summary>
    /// <param name="taskName">The stable Task Scheduler name owned by AppSupervisor.</param>
    public WindowsStartupTaskScheduler(string taskName)
    {
        _taskName = taskName;
    }

    /// <summary>
    /// Reads the existing task properties needed to decide whether registration is current and safe.
    /// </summary>
    /// <returns>The current task registration, or <see langword="null"/> when the task does not exist.</returns>
    public StartupTaskRegistration? GetRegistration()
    {
        object? serviceObject = null;
        object? folderObject = null;
        object? taskObject = null;
        object? definitionObject = null;
        object? principalObject = null;
        object? settingsObject = null;
        object? actionsObject = null;
        object? actionObject = null;
        object? triggersObject = null;
        object? triggerObject = null;

        try
        {
            serviceObject = CreateSchedulerService();
            dynamic service = serviceObject;
            service.Connect();

            folderObject = service.GetFolder(RootFolderPath);
            dynamic folder = folderObject;
            taskObject = folder.GetTask(_taskName);
            dynamic task = taskObject;

            definitionObject = task.Definition;
            dynamic definition = definitionObject;
            principalObject = definition.Principal;
            dynamic principal = principalObject;
            settingsObject = definition.Settings;
            dynamic settings = settingsObject;
            actionsObject = definition.Actions;
            dynamic actions = actionsObject;
            triggersObject = definition.Triggers;
            dynamic triggers = triggersObject;

            int actionCount = Convert.ToInt32(actions.Count);
            int triggerCount = Convert.ToInt32(triggers.Count);
            string executablePath = "";
            string workingDirectory = "";
            bool logonTriggerEnabled = false;
            string triggerUserId = "";

            if (actionCount > 0)
            {
                actionObject = actions.Item(1);
                dynamic action = actionObject;

                if (Convert.ToInt32(action.Type) == TaskActionExecute)
                {
                    executablePath = Convert.ToString(action.Path) ?? "";
                    workingDirectory = Convert.ToString(action.WorkingDirectory) ?? "";
                }
            }

            if (triggerCount > 0)
            {
                triggerObject = triggers.Item(1);
                dynamic trigger = triggerObject;
                logonTriggerEnabled =
                    Convert.ToInt32(trigger.Type) == TaskTriggerLogon &&
                    Convert.ToBoolean(trigger.Enabled);
                triggerUserId = Convert.ToString(trigger.UserId) ?? "";
            }

            return new StartupTaskRegistration(
                executablePath,
                workingDirectory,
                Convert.ToString(principal.UserId) ?? "",
                triggerUserId,
                Convert.ToBoolean(task.Enabled),
                logonTriggerEnabled,
                Convert.ToInt32(principal.RunLevel) == TaskRunLevelHighest,
                Convert.ToInt32(settings.MultipleInstances) == TaskInstancesIgnoreNew,
                actionCount,
                triggerCount
            );
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (COMException ex) when (ex.HResult == ErrorFileNotFoundHResult)
        {
            return null;
        }
        finally
        {
            ReleaseComObject(triggerObject);
            ReleaseComObject(triggersObject);
            ReleaseComObject(actionObject);
            ReleaseComObject(actionsObject);
            ReleaseComObject(settingsObject);
            ReleaseComObject(principalObject);
            ReleaseComObject(definitionObject);
            ReleaseComObject(taskObject);
            ReleaseComObject(folderObject);
            ReleaseComObject(serviceObject);
        }
    }

    /// <summary>
    /// Creates or replaces the current-user logon task with highest privileges and duplicate-launch suppression.
    /// </summary>
    /// <param name="executablePath">The full AppSupervisor executable path executed at sign-in.</param>
    /// <param name="workingDirectory">The directory made current before AppSupervisor starts.</param>
    /// <param name="userId">The current Windows user SID used for both the principal and logon trigger.</param>
    public void Register(
        string executablePath,
        string workingDirectory,
        string userId)
    {
        object? serviceObject = null;
        object? folderObject = null;
        object? definitionObject = null;
        object? registrationInfoObject = null;
        object? principalObject = null;
        object? settingsObject = null;
        object? triggersObject = null;
        object? triggerObject = null;
        object? actionsObject = null;
        object? actionObject = null;
        object? registeredTaskObject = null;

        try
        {
            serviceObject = CreateSchedulerService();
            dynamic service = serviceObject;
            service.Connect();

            folderObject = service.GetFolder(RootFolderPath);
            dynamic folder = folderObject;
            definitionObject = service.NewTask(0);
            dynamic definition = definitionObject;

            registrationInfoObject = definition.RegistrationInfo;
            dynamic registrationInfo = registrationInfoObject;
            registrationInfo.Author = "AppSupervisor";
            registrationInfo.Description = "Starts AppSupervisor with administrator rights when this user signs in.";

            principalObject = definition.Principal;
            dynamic principal = principalObject;
            principal.UserId = userId;
            principal.LogonType = TaskLogonInteractiveToken;
            principal.RunLevel = TaskRunLevelHighest;

            settingsObject = definition.Settings;
            dynamic settings = settingsObject;
            settings.Enabled = true;
            settings.AllowDemandStart = true;
            settings.StartWhenAvailable = true;
            settings.DisallowStartIfOnBatteries = false;
            settings.StopIfGoingOnBatteries = false;
            settings.ExecutionTimeLimit = "PT0S";
            settings.MultipleInstances = TaskInstancesIgnoreNew;

            triggersObject = definition.Triggers;
            dynamic triggers = triggersObject;
            triggerObject = triggers.Create(TaskTriggerLogon);
            dynamic trigger = triggerObject;
            trigger.Id = "CurrentUserLogon";
            trigger.Enabled = true;
            trigger.UserId = userId;

            actionsObject = definition.Actions;
            dynamic actions = actionsObject;
            actionObject = actions.Create(TaskActionExecute);
            dynamic action = actionObject;
            action.Path = executablePath;
            action.WorkingDirectory = workingDirectory;

            registeredTaskObject = folder.RegisterTaskDefinition(
                _taskName,
                definition,
                TaskCreateOrUpdate,
                userId,
                null,
                TaskLogonInteractiveToken,
                null
            );
        }
        finally
        {
            ReleaseComObject(registeredTaskObject);
            ReleaseComObject(actionObject);
            ReleaseComObject(actionsObject);
            ReleaseComObject(triggerObject);
            ReleaseComObject(triggersObject);
            ReleaseComObject(settingsObject);
            ReleaseComObject(principalObject);
            ReleaseComObject(registrationInfoObject);
            ReleaseComObject(definitionObject);
            ReleaseComObject(folderObject);
            ReleaseComObject(serviceObject);
        }
    }

    /// <summary>
    /// Instantiates the operating system's Task Scheduler automation service.
    /// </summary>
    /// <returns>The COM service object before it is connected.</returns>
    private static object CreateSchedulerService()
    {
        Type serviceType = Type.GetTypeFromProgID(SchedulerProgId, throwOnError: true)
            ?? throw new InvalidOperationException("Windows Task Scheduler is unavailable.");

        return Activator.CreateInstance(serviceType)
            ?? throw new InvalidOperationException("Windows Task Scheduler could not be created.");
    }

    /// <summary>
    /// Releases one Task Scheduler runtime callable wrapper as soon as it is no longer needed.
    /// </summary>
    /// <param name="value">The possible COM wrapper to release.</param>
    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
            Marshal.FinalReleaseComObject(value);
    }
}
