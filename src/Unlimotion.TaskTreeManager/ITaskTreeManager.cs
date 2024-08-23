using Unlimotion.Server.Domain;

namespace Unlimotion.TaskTree;

public interface ITaskTreeManager
{
    public Task<List<TaskItem>> AddTask(TaskItem change, TaskItem? currentTask = null,
                                        bool isBlocked = false);

    public Task<List<TaskItem>> AddChildTask(TaskItem change, string currentTask);

    public Task<List<TaskItem>> DeleteTask(TaskItem change, bool deleteInStorage = true);

    public Task UpdateTask(TaskItem change);

    public Task<List<TaskItem>> CloneTask(TaskItem change, List<TaskItem> stepParents);

    public Task<List<TaskItem>> AddNewParentToTask(string changeId, string additionalParentId);

    public Task<List<TaskItem>> MoveTaskToNewParent(string changeId, string newParentId, string? prevParentId);

    public Task<List<TaskItem>> UnblockTask(string taskToUnblockId, string blockingTaskId);

    public Task<List<TaskItem>> BlockTask(string taskToBlockId, string blockingTaskId);

    public Task<TaskItem> LoadTask(string taskId);

    public Task<List<TaskItem>> DeleteParentChildRelation(string parentId, string childId);

}

