﻿using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Linq.Dynamic;
using System.Threading.Tasks;
using Abp.Application.Services;
using Abp.Application.Services.Dto;
using Abp.Authorization;
using Abp.AutoMapper;
using Abp.Domain.Repositories;
using Abp.Extensions;
using Abp.Linq.Extensions;
using Abp.Net.Mail.Smtp;
using Abp.Notifications;
using Abp.Runtime.Session;
using Abp.Threading;
using Abp.Timing;
using AutoMapper;
using LearningMpaAbp.Authorization;
using LearningMpaAbp.Tasks.Dtos;
using LearningMpaAbp.Users;
using LearningMpaAbp.Extensions;

namespace LearningMpaAbp.Tasks
{
    /// <summary>
    ///     Implements <see cref="ITaskAppService" /> to perform task related application functionality.
    ///     Inherits from <see cref="ApplicationService" />.
    ///     <see cref="ApplicationService" /> contains some basic functionality common for application services (such as
    ///     logging and localization).
    /// </summary>
    public class TaskAppService : LearningMpaAbpAppServiceBase, ITaskAppService
    {
        private readonly INotificationPublisher _notificationPublisher;
        private readonly ISmtpEmailSenderConfiguration _smtpEmialSenderConfig;
        //These members set in constructor using constructor injection.

        private readonly IRepository<Task> _taskRepository;
        private readonly IRepository<User, long> _userRepository;

        private readonly ITaskCache _taskCache;

        /// <summary>
        ///     In constructor, we can get needed classes/interfaces.
        ///     They are sent here by dependency injection system automatically.
        /// </summary>
        public TaskAppService(IRepository<Task> taskRepository, IRepository<User, long> userRepository,
            ISmtpEmailSenderConfiguration smtpEmialSenderConfigtion, INotificationPublisher notificationPublisher, ITaskCache taskCache)
        {
            _taskRepository = taskRepository;
            _userRepository = userRepository;
            _smtpEmialSenderConfig = smtpEmialSenderConfigtion;
            _notificationPublisher = notificationPublisher;
            _taskCache = taskCache;
        }

        public TaskCacheItem GetTaskFromCacheById(int taskId)
        {
            return _taskCache[taskId];
        }

        public IList<TaskDto> GetAllTasks()
        {
            var tasks = _taskRepository.GetAll().OrderByDescending(t => t.CreationTime).ToList();
            return Mapper.Map<IList<TaskDto>>(tasks);
        }

        public GetTasksOutput GetTasks(GetTasksInput input)
        {
            var query = _taskRepository.GetAll().Include(t => t.AssignedPerson)
                .WhereIf(input.State.HasValue, t => t.State == input.State.Value)
                .WhereIf(!input.Filter.IsNullOrEmpty(), t => t.Title.Contains(input.Filter))
                .WhereIf(input.AssignedPersonId.HasValue, t => t.AssignedPersonId == input.AssignedPersonId.Value);

            //排序
            if (!string.IsNullOrEmpty(input.Sorting))
                query = query.OrderBy(input.Sorting);
            else
                query = query.OrderByDescending(t => t.CreationTime);

            var taskList = query.ToList();

            //Used AutoMapper to automatically convert List<Task> to List<TaskDto>.
            return new GetTasksOutput
            {
                Tasks = Mapper.Map<List<TaskDto>>(taskList)
            };
        }


        public PagedResultDto<TaskDto> GetPagedTasks(GetTasksInput input)
        {
            //初步过滤
            var query = _taskRepository.GetAll().Include(t => t.AssignedPerson)
                .WhereIf(input.State.HasValue, t => t.State == input.State.Value)
                .WhereIf(!input.Filter.IsNullOrEmpty(), t => t.Title.Contains(input.Filter))
                .WhereIf(input.AssignedPersonId.HasValue, t => t.AssignedPersonId == input.AssignedPersonId.Value);

            //排序
            query = !string.IsNullOrEmpty(input.Sorting) ? query.OrderBy(input.Sorting) : query.OrderByDescending(t => t.CreationTime);

            //获取总数
            var tasksCount = query.Count();
            //默认的分页方式
            //var taskList = query.Skip(input.SkipCount).Take(input.MaxResultCount).ToList();

            //ABP提供了扩展方法PageBy分页方式
            var taskList = query.PageBy(input).ToList();

            return new PagedResultDto<TaskDto>(tasksCount, taskList.MapTo<List<TaskDto>>());
        }

        public async Task<TaskDto> GetTaskByIdAsync(int taskId)
        {
            //Called specific GetAllWithPeople method of task repository.
            var task = await _taskRepository.GetAsync(taskId);

            //Used AutoMapper to automatically convert List<Task> to List<TaskDto>.
            return task.MapTo<TaskDto>();
        }

        public TaskDto GetTaskById(int taskId)
        {
            var task = _taskRepository.Get(taskId);

            return task.MapTo<TaskDto>();
        }
        
        public void UpdateTask(UpdateTaskInput input)
        {
            //We can use Logger, it's defined in ApplicationService base class.
            Logger.Info("Updating a task for input: " + input);

            //获取是否有权限
            bool canAssignTaskToOther = PermissionChecker.IsGranted(PermissionNames.Pages_Tasks_AssignPerson);
            //如果任务已经分配且未分配给自己，且不具有分配任务权限，则抛出异常
            if (input.AssignedPersonId.HasValue && input.AssignedPersonId.Value != AbpSession.GetUserId() && !canAssignTaskToOther)
            {
                throw new AbpAuthorizationException("没有分配任务给他人的权限！");
            }

            var updateTask = Mapper.Map<Task>(input);
            _taskRepository.Update(updateTask);
        }
        
        public int CreateTask(CreateTaskInput input)
        {
            //We can use Logger, it's defined in ApplicationService class.
            Logger.Info("Creating a task for input: " + input);
            
            //判断用户是否有权限
            if (input.AssignedPersonId.HasValue && input.AssignedPersonId.Value !=AbpSession.GetUserId())
                PermissionChecker.Authorize(PermissionNames.Pages_Tasks_AssignPerson);

            var task = Mapper.Map<Task>(input);

            int result = _taskRepository.InsertAndGetId(task);

            //只有创建成功才发送邮件和通知
            if (result > 0)
            {
                task.CreationTime = Clock.Now;

                if (input.AssignedPersonId.HasValue)
                {
                    task.AssignedPerson = _userRepository.Load(input.AssignedPersonId.Value);
                    var message = "You hava been assigned one task into your todo list.";

                    //TODO:需要重新配置QQ邮箱密码
                    //SmtpEmailSender emailSender = new SmtpEmailSender(_smtpEmialSenderConfig);
                    //emailSender.Send("ysjshengjie@qq.com", task.AssignedPerson.EmailAddress, "New Todo item", message);

                    _notificationPublisher.Publish("NewTask", new MessageNotificationData(message), null,
                        NotificationSeverity.Info, new[] { task.AssignedPerson.ToUserIdentifier() });
                }
            }

            return result;
        }

        [AbpAuthorize(PermissionNames.Pages_Tasks_Delete)]
        public void DeleteTask(int taskId)
        {
            var task = _taskRepository.Get(taskId);
            if (task != null)
                _taskRepository.Delete(task);
        }
    }
}