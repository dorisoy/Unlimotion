using Polly;
using Polly.Retry;
using System.Collections.Generic;
using Unlimotion.Server.Domain;

namespace Unlimotion.TaskTree;

public class TaskTreeManager : ITaskTreeManager
{
    private IStorage Storage { get; init; }
    public TaskTreeManager(IStorage storage)
    {
        Storage = storage;
    }
    public async Task<List<TaskItem>> AddTask(TaskItem change, TaskItem? currentTask = null, bool isBlocked = false)
    {
        var result = new List<TaskItem>();

        //Create
        if (currentTask is null)
        {
            await IsCompletedAsync(async Task<bool> () =>
            {
                try
                {
                    change.PrevVersion = false;
                    change.SortOrder = DateTime.Now;
                    await Storage.Save(change);
                    result.Add(change);

                    return true;
                }
                catch
                {
                    return false;
                }
            });
            return result;
        }
        //CreateSibling, CreateBlockedSibling
        else
        {
            string newTaskId = null;

            await IsCompletedAsync(async Task<bool> () =>
            {
                try
                {
                    if (newTaskId is null)
                    {
                        change.PrevVersion = false;
                        await Storage.Save(change);
                        newTaskId = change.Id;
                    }

                    if (currentTask != null)
                    {
                        (currentTask.ParentTasks ?? [])
                        .ForEach(async parent => result.AddRange(
                            await CreateParentChildRelation(parent, newTaskId)));
                    }    
   
                    if (isBlocked && currentTask != null)
                    {
                        result.AddRange(
                            await CreateBlockingBlockedByRelation(change.Id, currentTask.Id));                        
                    }

                    return true;
                }
                catch
                {
                    return false;
                }
            });


            return result;
        }
    }
    public async Task<List<TaskItem>> AddChildTask(TaskItem change, string currentTaskId)
    {
        var result = new List<TaskItem>();
        string newTaskId = null;

        //CreateInner
        await IsCompletedAsync(async Task<bool> () =>
        {
            try
            {
                if (newTaskId is null)
                {
                    change.PrevVersion = false;
                    await Storage.Save(change);
                    newTaskId = change.Id;
                }

                result.AddRange(await CreateParentChildRelation(currentTaskId, change.Id));                

                return true;
            }
            catch
            {
                return false;
            }
        });

        return result;
    }
    public async Task<List<TaskItem>> DeleteTask(TaskItem change, bool deleteInStorage = true)
    {
        var result = new List<TaskItem>();

        await IsCompletedAsync(async () =>
        {
            try
            {
                //удалить во всех детях ссылки в Parents на удаляемый таск
                var deletingTask = (deleteInStorage) ? await Storage.Load(change.Id) : change;

                if (deletingTask is not null)
                {
                    if (deletingTask.ContainsTasks.Any())
                    {
                        deletingTask.ContainsTasks
                        .ForEach(async child => result.AddRange(
                            await BreakParentChildRelation(deletingTask.Id, child)));
                    }
                    //удалить во всех grandParents ссылки в Contains на удаляемый таск
                    if (deletingTask.ParentTasks.Any())
                    {
                        deletingTask.ParentTasks
                        .ForEach(async parent => result.AddRange(
                            await BreakParentChildRelation(parent, deletingTask.Id)));
                    }

                    //удалить во всех блокирующих тасках ссылку на удаляемый таск
                    if (deletingTask.BlockedByTasks.Any())
                    {
                        deletingTask.BlockedByTasks
                        .ForEach(async blocker => result.AddRange(
                            await BreakBlockingBlockedByRelation(deletingTask.Id, blocker)));                    
                    }

                    //удалить во всех блокируемых тасках ссылку на удаляемый таск 
                    if (deletingTask.BlocksTasks.Any())
                    {
                        deletingTask.BlocksTasks
                        .ForEach(async blocked => result.AddRange(
                            await BreakBlockingBlockedByRelation(blocked, deletingTask.Id)));                        
                    }

                    //удалить сам таск из БД
                    if (deleteInStorage) await Storage.Remove(change.Id);
                }

                return true;
            }
            catch
            {
                return false;
            };
        });

        return result;
    }
    public async Task UpdateTask(TaskItem change)
    {
        await IsCompletedAsync(async Task<bool> () =>
        {
            try
            {
                await Storage.Save(change);
                return true;
            }
            catch
            {
                return false;
            }
        });
    }
    public async Task<List<TaskItem>> CloneTask(TaskItem change, List<TaskItem> stepParents)
    {
        var result = new List<TaskItem>();
        string newTaskId = null;

        await IsCompletedAsync(async Task<bool> () =>
        {
            try
            {
                if (newTaskId is null)
                {
                    change.PrevVersion = false;
                    await Storage.Save(change);
                    newTaskId = change.Id;
                }

                stepParents.ForEach(async parent => result.AddRange(
                await CreateParentChildRelation(parent.Id, newTaskId)));                                

                return true;
            }
            catch
            {
                return false;
            }
        });

        return result;
    }
    public async Task<List<TaskItem>> AddNewParentToTask(string changeId, string additionalParentId)
    {
        var result = new List<TaskItem>();
        result.AddRange(await CreateParentChildRelation(additionalParentId, changeId));
        
        return result;
    }
    public async Task<List<TaskItem>> MoveTaskToNewParent(string changeId, string newParentId, string? prevParentId)
    {
        var result = new List<TaskItem>();
        if (prevParentId is not null)
        {
            result.AddRange(await BreakParentChildRelation(prevParentId, changeId));
        }
        result.AddRange(await CreateParentChildRelation(newParentId, changeId));

        return result;
    }
    public async Task<List<TaskItem>> UnblockTask(string taskToUnblockId, string blockingTaskId)
    {
        var result = new List<TaskItem>();

        result.AddRange(await BreakBlockingBlockedByRelation(taskToUnblockId, blockingTaskId));       

        return result;
    }

    public async Task<List<TaskItem>> BlockTask(string taskToBlockId, string blockingTaskId)
    {
        var result = new List<TaskItem>();

        result.AddRange(await CreateBlockingBlockedByRelation(taskToBlockId, blockingTaskId));      

        return result;
    }

    public async Task<TaskItem> LoadTask(string taskId)
    {
        var task = await Storage.Load(taskId);

        if (!task.PrevVersion) 
            return task;

        if (task.ContainsTasks is not null)
        {
           foreach (var childTask in task.ContainsTasks)
           {
               var childItem = await Storage.Load(childTask);

               if (childItem != null 
                    && !(childItem.ParentTasks ?? []).Contains(task.Id))
               {
                  childItem.ParentTasks!.Add(task.Id);
                  await Storage.Save(childItem);
               }
           }
        }

        if (task.BlocksTasks is not null)
        {
           foreach (var blockedTask in task.BlocksTasks)
           {
              var blockedItem = await Storage.Load(blockedTask);

              if (!(blockedItem.BlockedByTasks ?? []).Contains(task.Id))
              {
                 blockedItem.BlockedByTasks!.Add(task.Id);
                 await Storage.Save(blockedItem);
              }
           }
        }

        task.PrevVersion = false;
        await Storage.Save(task);

        return task;
    }

    public async Task<List<TaskItem>> DeleteParentChildRelation(string parentId, string childId)
    {
        var result = new List<TaskItem>();
        
        result.AddRange(await BreakParentChildRelation(parentId, childId));
        
        return result;
    }

    private async Task<List<TaskItem>> BreakParentChildRelation(string parentId, string childId)
    {
        var result = new List<TaskItem>();

        await IsCompletedAsync(async () =>
        {
            try
            {
                var parentTaskItem = await Storage.Load(parentId);
                if (parentTaskItem != null && parentTaskItem.ContainsTasks.Contains(childId))
                {
                    parentTaskItem.ContainsTasks.Remove(childId);
                    await Storage.Save(parentTaskItem);
                    result.Add(parentTaskItem);
                }

                var childTaskItem = await Storage.Load(childId);
                if (childTaskItem != null && (childTaskItem.ParentTasks ?? []).Contains(parentId))
                {
                    childTaskItem.ParentTasks!.Remove(parentId);
                    await Storage.Save(childTaskItem);
                    result.Add(childTaskItem);
                }

                return true;
            }
            catch
            {
                return false;
            };
        });

        return result;
    }

    private async Task<List<TaskItem>> CreateParentChildRelation(string parentId, string childId)
    {
        var result = new List<TaskItem>();

        await IsCompletedAsync(async () =>
        {
            try
            {
                var parentTaskItem = await Storage.Load(parentId);
                if (parentTaskItem != null && !parentTaskItem.ContainsTasks.Contains(childId))
                {
                    parentTaskItem.ContainsTasks.Add(childId);
                    parentTaskItem.SortOrder = DateTime.Now;
                    await Storage.Save(parentTaskItem);
                    result.Add(parentTaskItem);
                }

                var childTaskItem = await Storage.Load(childId);
                if (childTaskItem != null && !(childTaskItem.ParentTasks ?? []).Contains(parentId))
                {
                    childTaskItem.ParentTasks!.Add(parentId);
                    childTaskItem.SortOrder = DateTime.Now;
                    await Storage.Save(childTaskItem);
                    result.Add(childTaskItem);
                }

                return true;
            }
            catch
            {
                return false;
            };
        });

        return result;
    }

    private async Task<List<TaskItem>> CreateBlockingBlockedByRelation(string taskToBlockId, string blockingTaskId)
    {
        var result = new List<TaskItem>();

        await IsCompletedAsync(async () =>
        {
            try
            {
                var blockingTaskItem = await Storage.Load(blockingTaskId);
                if (!blockingTaskItem.BlocksTasks.Contains(taskToBlockId))
                {
                    blockingTaskItem.BlocksTasks.Add(taskToBlockId);
                    blockingTaskItem.SortOrder = DateTime.Now;
                    await Storage.Save(blockingTaskItem);
                    result.Add(blockingTaskItem);
                }

                var taskToBlockItem = await Storage.Load(taskToBlockId);
                if (!taskToBlockItem.BlockedByTasks.Contains(blockingTaskId))
                {
                    taskToBlockItem.BlockedByTasks.Add(blockingTaskId);
                    taskToBlockItem.SortOrder = DateTime.Now;
                    await Storage.Save(taskToBlockItem);
                    result.Add(taskToBlockItem);
                }

                return true;
            }
            catch
            {
                return false;
            };
        });

        return result;
    }

    private async Task<List<TaskItem>> BreakBlockingBlockedByRelation(string taskToUnblockId, string blockingTaskId)
    {
        var result = new List<TaskItem>();

        await IsCompletedAsync(async () =>
        {
            try
            {
                var blockingTaskItem = await Storage.Load(blockingTaskId);
                if (blockingTaskItem.BlocksTasks.Contains(taskToUnblockId))
                {
                    blockingTaskItem.BlocksTasks.Remove(taskToUnblockId);
                    await Storage.Save(blockingTaskItem);
                    result.Add(blockingTaskItem);
                }

                var taskToUnblockItem = await Storage.Load(taskToUnblockId);
                if (taskToUnblockItem.BlockedByTasks.Contains(blockingTaskId))
                {
                    taskToUnblockItem.BlockedByTasks.Remove(blockingTaskId);
                    await Storage.Save(taskToUnblockItem);
                    result.Add(taskToUnblockItem);
                }

                return true;
            }
            catch
            {
                return false;
            };
        });

        return result;
    }

    private async Task<bool> IsCompletedAsync(Func<Task<bool>> task, TimeSpan? timeout = null)
    {
        TimeSpan countRetry = timeout ?? TimeSpan.FromMinutes(2);

        AsyncRetryPolicy<bool>? retryPolicy = Policy.HandleResult<bool>(x => !x)
            .WaitAndRetryAsync(
                (int)countRetry.TotalSeconds, _ => TimeSpan.FromSeconds(1), (_, _, count,
                    _) =>
                {
                    //_logger.Error($"Попытка выполнения операции с таском  №{count}");
                });

        var res = await retryPolicy.ExecuteAsync(() => task.Invoke());

        if (res == false)
            throw new TimeoutException(
                $"Операция не была корректно завершена за заданный таймаут {timeout}");
        return (res);
    }
}


