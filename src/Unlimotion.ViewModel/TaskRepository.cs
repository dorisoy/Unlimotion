using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using DynamicData;
using DynamicData.Binding;

namespace Unlimotion.ViewModel
{
    public interface ITaskRepository
    {
        SourceCache<TaskItemViewModel, string> Tasks { get; }
        SourceCache<ComputedTaskInfo, string> ComputedTasksInfo { get; }
        void Init();
        Task Remove(string itemId);
        Task Save(TaskItem item);
        Task SaveComputedTaskInfo(ComputedTaskInfo info);
        IObservable<IChangeSet<TaskItemViewModel, string>> GetRoots();
        TaskItemViewModel Clone(TaskItem clone, params TaskItemViewModel[] destinations);
    }

    public class TaskRepository : ITaskRepository
    {
        private readonly ITaskStorage _taskStorage;
        public SourceCache<TaskItemViewModel, string> Tasks { get; private set; }
        public SourceCache<ComputedTaskInfo, string> ComputedTasksInfo { get; private set; }
        IObservable<Func<TaskItemViewModel, bool>> rootFilter;

        public TaskRepository(ITaskStorage taskStorage)
        {
            _taskStorage = taskStorage;
        }
		
        public async Task Remove(string itemId)
        {
            Tasks.Remove(itemId);
            await _taskStorage.Remove(itemId);
        }

        public async Task Save(TaskItem item)
        {
            await _taskStorage.Save(item);
        }

        public async Task SaveComputedTaskInfo(ComputedTaskInfo info)
        {
	        await _taskStorage.SaveComputedTaskInfo(info);
        }

        public void Init() => Init(_taskStorage.GetAll(), _taskStorage.GetTasksComputedInfo());
        
        private void Init(IEnumerable<TaskItem> items, IEnumerable<ComputedTaskInfo> tasksInfo)
        {
	        Tasks = new(item => item.Id);
	        ComputedTasksInfo = new(item => item.TaskId);
	        
            foreach (var taskItem in items)
            {
                var vm = new TaskItemViewModel(taskItem, this);
                Tasks.AddOrUpdate(vm);
            }

            foreach (var taskInfo in tasksInfo)
            {
                ComputedTasksInfo.AddOrUpdate(taskInfo);
            }

            foreach (var child in Tasks.Items)
            {
                foreach (var blockingParent in child.ParentsTasks.Where(e => e.BlockedByTasks.Any()))
                {
                    var taskInfo = ComputedTasksInfo.Items.FirstOrDefault(e => e.TaskId == child.Id);

                    if (taskInfo == null)
                        taskInfo = new ComputedTaskInfo { TaskId = child.Id };

                    if (!taskInfo.FromIds.Contains(blockingParent.Id))
                    {
                        taskInfo.FromIds.Add(blockingParent.Id);
                        ComputedTasksInfo.AddOrUpdate(taskInfo);
                        SaveComputedTaskInfo(taskInfo);
                    }
                }
            }

            rootFilter = Tasks.Connect()
				.AutoRefreshOnObservable(t => t.Contains.ToObservableChangeSet())
				.TransformMany(item =>
				{
					var many = item.Contains.Where(s => !string.IsNullOrEmpty(s)).Select(id => id);
					return many;
				}, s => s)
				
				.Distinct(k => k)
				.ToCollection()
				.Select(items =>
				{
					bool Predicate(TaskItemViewModel task) => items.Count == 0 || items.All(t => t != task.Id);
					return (Func<TaskItemViewModel, bool>)Predicate;
				});
        }
		
		public TaskItemViewModel GetById(string id)
		{
			var item = Tasks.Lookup(id);
			return item.HasValue ? item.Value : null;
		}
		
		public IObservable<IChangeSet<TaskItemViewModel, string>> GetRoots()
		{
			IObservable<IChangeSet<TaskItemViewModel, string>> roots;
			roots = Tasks.Connect()
				.Filter(rootFilter);
			return roots;
		}

		public TaskItemViewModel Clone(TaskItem clone, params TaskItemViewModel[] destinations)
		{
			var task = new TaskItemViewModel(clone, this);
			task.SaveItemCommand.Execute();
			foreach (var destination in destinations)
			{
				destination.Contains.Add(task.Id);
			}
			this.Tasks.AddOrUpdate(task);
			return task;
		}
	}
}
