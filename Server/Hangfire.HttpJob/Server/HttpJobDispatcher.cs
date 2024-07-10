﻿using Hangfire.Dashboard;
using Hangfire.Logging;
using Hangfire.Storage;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;
using Hangfire.Common;
using Hangfire.HttpJob.Server.JobAgent;
using Hangfire.HttpJob.Support;
using Hangfire.States;


namespace Hangfire.HttpJob.Server
{
	public class HttpJobDispatcher : IDashboardDispatcher
	{

		private static readonly ILog Logger = LogProvider.For<HttpJobDispatcher>();


		public async Task Dispatch(DashboardContext context)
		{
			if (context == null)
				throw new ArgumentNullException(nameof(context));
			try
			{
				if (!"POST".Equals(context.Request.Method, StringComparison.OrdinalIgnoreCase))
				{
					context.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
					return;
				}

				//操作类型
				var op = context.Request.GetQuery("op");
				if (string.IsNullOrEmpty(op))
				{
					context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
					return;
				}

				op = op.ToLower();

				switch (op)
				{
					// dashbord 上获取周期性job详情
					case "getrecurringjob":
						await GetRecurringJobDetail(context);
						return;
					// dashbord 上获取Agent job详情
					case "getbackgroundjobdetail":
						await DoGetBackGroundJobDetail(context);
						return;
					// 删除job
					case "deljob":
						await DelJob(context);
						return;
					// 暂停或开始job
					case "pausejob":
						await DoPauseOrRestartJob(context);
						return;
					//新增后台任务job
					case "backgroundjob":
						await AddBackgroundjob(context);
						return;
					//新增周期性任务job 只新增有重复的话报错
					case "addrecurringjob":
						await AddRecurringJob(context);
						return;
					case "recurringjob":
					//新增或修改周期性任务job
					case "editrecurringjob":
						await AddOrUpdateRecurringJob(context);
						return;
					//启动job
					case "startbackgroundjob":
						await StartBackgroudJob(context);
						return;
					//暂停job
					case "stopbackgroundjob":
						await StopBackgroudJob(context);
						return;
					//获取全局配置
					case "getglobalsetting":
						await GetGlobalSetting(context);
						return;
					//保存全局配置
					case "saveglobalsetting":
						await SaveGlobalSetting(context);
						return;
					//获取agentserver列表
					case "getagentserver":
						await GetAgentServer(context);
						return;
					//导出
					case "exportjobs":
						await ExportJobsAsync(context);
						return;
					//导入
					case "importjobs":
						await ImportJobsAsync(context);
						return;
					//分页获取周期性任务
					case "getrecurringjobs":
						await RecurringJobsAsync(context);
						return;
					case "getpasusejobcron":
						await GetPauseJobCron(context);
						return;
					default:
						context.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
						break;
				}
			}
			catch (Exception ex)
			{
				Logger.ErrorException("HttpJobDispatcher.Dispatch", ex);
				context.Response.ContentType = "application/json";
				context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
				return;
			}
		}

		/// <summary>
		/// 获取agent服务器
		/// </summary>
		/// <param name="context"></param>
		/// <returns></returns>
		private async Task GetAgentServer(DashboardContext context)
		{
			try
			{
				var html = JobAgentHeartBeatServer.GetAgentServerListHtml(context.GetCurrentHangfireUrl());
				await context.Response.WriteAsync(html);
			}
			catch (Exception e)
			{
				await context.Response.WriteAsync("err:" + e.Message);
			}
		}

		/// <summary>
		/// 保存全局配置
		/// </summary>
		/// <param name="context"></param>
		/// <returns></returns>
		private async Task SaveGlobalSetting(DashboardContext context)
		{
			try
			{
				var contentBody = await GetRequestBody<string>(context);

				if (string.IsNullOrEmpty(contentBody.Item1))
				{
					await context.Response.WriteAsync($"err: json invaild:{contentBody.Item2}");
					return;
				}

				var jsonString = ConvertJsonString(contentBody.Item1);
				if (string.IsNullOrEmpty(jsonString))
				{
					await context.Response.WriteAsync($"err: invaild json !");
					return;
				}

				File.WriteAllText(CodingUtil.HangfireHttpJobOptions.GlobalSettingJsonFilePath, jsonString);

				CodingUtil.GetGlobalAppsettings();
			}
			catch (Exception e)
			{
				await context.Response.WriteAsync("err:" + e.Message);
			}
		}

		/// <summary>
		/// 获取全局配置
		/// </summary>
		/// <param name="context"></param>
		/// <returns></returns>
		private async Task GetGlobalSetting(DashboardContext context)
		{
			var path = CodingUtil.HangfireHttpJobOptions.GlobalSettingJsonFilePath;
			try
			{
				if (!File.Exists(path))
				{
					File.WriteAllText(path, "");//如果没有权限则会报错
					await context.Response.WriteAsync("");
					return;
				}

				var content = File.ReadAllText(path);
				await context.Response.WriteAsync(content);
			}
			catch (Exception e)
			{
				await context.Response.WriteAsync($"err:{nameof(HangfireHttpJobOptions.GlobalSettingJsonFilePath)}:[{path}] access error:{e.Message}");
			}
		}

		/// <summary>
		/// 新增周期job 只是新增 如果已存在了就不新增了
		/// </summary>
		/// <param name="context"></param>
		/// <returns></returns>
		private async Task AddRecurringJob(DashboardContext context)
		{
			var jobItemRt = await GetCheckedJobItem(context);
			if (!string.IsNullOrEmpty(jobItemRt.Item2))
			{
				context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
				await context.Response.WriteAsync(jobItemRt.Item2);
				return;
			}
			if (CodingUtil.HangfireHttpJobOptions.AddHttpJobFilter != null)
			{
				if (!CodingUtil.HangfireHttpJobOptions.AddHttpJobFilter(jobItemRt.Item1))
				{
					context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
					await context.Response.WriteAsync("HttpJobFilter return false");
					return;
				}
			}

			if (string.IsNullOrEmpty(jobItemRt.Item1.QueueName))
			{
				jobItemRt.Item1.QueueName = CodingUtil.HangfireHttpJobOptions.DefaultRecurringQueueName;
			}
			var result = AddHttprecurringjob(jobItemRt.Item1, true);
			if (string.IsNullOrEmpty(result))
			{
				JobAgentHeartBeatServer.Start(false);
				context.Response.ContentType = "application/json";
				context.Response.StatusCode = (int)HttpStatusCode.NoContent;
				return;
			}
			else
			{
				context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
				await context.Response.WriteAsync(result);
				return;
			}
		}

		/// <summary>
		/// 新增周期性job 如果已存在就更新
		/// </summary>
		/// <param name="context"></param>
		/// <returns></returns>
		/// <exception cref="NotImplementedException"></exception>
		private async Task AddOrUpdateRecurringJob(DashboardContext context)
		{
			var jobItemRt = await GetCheckedJobItem(context);
			if (!string.IsNullOrEmpty(jobItemRt.Item2))
			{
				context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
				await context.Response.WriteAsync(jobItemRt.Item2);
				return;
			}
			if (CodingUtil.HangfireHttpJobOptions.AddHttpJobFilter != null)
			{
				if (!CodingUtil.HangfireHttpJobOptions.AddHttpJobFilter(jobItemRt.Item1))
				{
					context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
					await context.Response.WriteAsync("HttpJobFilter return false");
					return;
				}
			}
			// 判断是否是暂停状态
			var isJobPaused = IsJobPaused(jobItemRt.Item1.getJobIdentifier());
			var lastCron = jobItemRt.Item1.Cron;
			if (isJobPaused)
			{
				jobItemRt.Item1.Cron = Cron.Never();
			}
			var result = AddHttprecurringjob(jobItemRt.Item1);
			if (string.IsNullOrEmpty(result))
			{
				if (isJobPaused)
				{
					// 如果之前是暂停 得更新一下cron为最新更改的 并且保持暂停不变
					jobItemRt.Item1.Cron = lastCron;
					PauseOrRestartJob(jobItemRt.Item1.getJobIdentifier(), jobItemRt.Item1);
				}
				JobAgentHeartBeatServer.Start(false);
				context.Response.ContentType = "application/json";
				context.Response.StatusCode = (int)HttpStatusCode.NoContent;
				return;
			}
			else
			{
				context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
				await context.Response.WriteAsync(result);
				return;
			}
		}


		/// <summary>
		/// 新增后台任务job
		/// </summary>
		/// <param name="context"></param>
		/// <returns></returns>
		/// <exception cref="NotImplementedException"></exception>
		private async Task AddBackgroundjob(DashboardContext context)
		{
			var jobItemRt = await GetCheckedJobItem(context);
			if (!string.IsNullOrEmpty(jobItemRt.Item2))
			{
				context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
				await context.Response.WriteAsync(jobItemRt.Item2);
				return;
			}
			if (CodingUtil.HangfireHttpJobOptions.AddHttpJobFilter != null)
			{
				if (!CodingUtil.HangfireHttpJobOptions.AddHttpJobFilter(jobItemRt.Item1))
				{
					context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
					await context.Response.WriteAsync("HttpJobFilter return false");
					return;
				}
			}

			var jobItem = jobItemRt.Item1;
			if (jobItem.DelayFromMinutes < -1)
			{
				context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
				await context.Response.WriteAsync("DelayFromMinutes invaild");
				return;
			}

			var jobId = AddHttpbackgroundjob(jobItem);
			if (!string.IsNullOrEmpty(jobId))
			{
				context.Response.ContentType = "application/json";
				context.Response.StatusCode = (int)HttpStatusCode.OK;
				await context.Response.WriteAsync(jobId);
				return;
			}

			context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
			await context.Response.WriteAsync("add fail");
			return;
		}

		/// <summary>
		/// 通用的检查并获取jobItem
		/// </summary>
		/// <param name="context"></param>
		/// <returns></returns>
		private async Task<(HttpJobItem, string)> GetCheckedJobItem(DashboardContext context)
		{
			var jobItemBody = await GetRequestBody<HttpJobItem>(context);
			var jobItem = jobItemBody.Item1;
			if (jobItem == null)
			{
				return (null, $"get job data fail:{jobItemBody.Item2}");
			}

			string CheckHttpJobItem(HttpJobItem item, bool isParent)
			{
				if (string.IsNullOrEmpty(item.Url) || item.Url.ToLower().Equals("http://"))
				{
					return ("Url invaild");
				}

				if (string.IsNullOrEmpty(item.ContentType))
				{
					return ("ContentType invaild");
				}


				if (isParent)
				{
					if (string.IsNullOrEmpty(item.JobName))
					{
						var jobName = item.Url.Split('/').LastOrDefault() ?? string.Empty;
						item.JobName = jobName;
					}

					if (string.IsNullOrEmpty(item.JobName))
					{
						return ("JobName invaild");
					}
				}

				return string.Empty;
			}

			var list = new List<HttpJobItem>();

			void AddAllJobItem(HttpJobItem item, List<HttpJobItem> listOut)
			{
				listOut.Add(item);
				if (item.Success != null)
				{
					AddAllJobItem(item.Success, listOut);
				}

				if (item.Fail != null)
				{
					AddAllJobItem(item.Fail, listOut);
				}
			}

			AddAllJobItem(jobItem, list);

			for (int i = 0; i < list.Count; i++)
			{
				var checkResult = CheckHttpJobItem(list[i], i == 0);
				if (!string.IsNullOrEmpty(checkResult))
				{
					return (null, checkResult);
				}
			}

			return (jobItem, null);
		}


		/// <summary>
		/// 获取周期性job详情
		/// </summary>
		/// <param name="context"></param>
		/// <returns></returns>
		private async Task GetRecurringJobDetail(DashboardContext context)
		{
			var jobItemBody = await GetRequestBody<HttpJobItem>(context);
			var jobItem = jobItemBody.Item1;
			if (jobItem == null || string.IsNullOrEmpty(jobItem.JobName))
			{
				context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
				await context.Response.WriteAsync($"invalid request body:{jobItemBody.Item2}");
				return;
			}

			var strdata = GetRecurringJobString(jobItem.JobName);
			if (!string.IsNullOrEmpty(strdata))
			{
				context.Response.ContentType = "application/json";
				context.Response.StatusCode = (int)HttpStatusCode.OK;
				await context.Response.WriteAsync(strdata);
				return;
			}
			else
			{
				context.Response.StatusCode = (int)HttpStatusCode.NotFound;
				await context.Response.WriteAsync($"jobName:{jobItem.JobName} not found");
				return;
			}
		}


		/// <summary>
		/// 获取jobDetail
		/// </summary>
		/// <param name="_context"></param>
		/// <returns></returns>
		public async Task<Tuple<T, string>> GetRequestBody<T>(DashboardContext _context)
		{
			try
			{
				Stream body = null;
				if (_context is AspNetCoreDashboardContext)
				{
					var context = _context.GetHttpContext();
					body = context.Request.Body;
				}
				else
				{
					//兼容netframework

					var contextType = _context.Request.GetType();

					//private readonly IOwinContext _context;
					var owinContext = contextType.GetField("_context", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(_context.Request);

					if (owinContext == null)
					{
						Logger.Warn($"HttpJobDispatcher.GetJobItem:: get data from DashbordContext err,DashboardContext:{contextType.FullName}");
						return new Tuple<T, string>(default(T), $"HttpJobDispatcher.GetJobItem:: get data from DashbordContext err,DashboardContext:{contextType.FullName}");
					}

					var request = owinContext.GetType().GetProperty("Request")?.GetValue(owinContext);

					if (request == null)
					{
						Logger.Warn($"HttpJobDispatcher.GetJobItem:: get data from DashbordContext err,OwinContext:{owinContext.GetType().FullName}");
						return new Tuple<T, string>(default(T), $"HttpJobDispatcher.GetJobItem:: get data from DashbordContext err,OwinContext:{owinContext.GetType().FullName}");
					}

					body = request.GetType().GetProperty("Body")?.GetValue(request) as Stream;
					if (body == null)
					{
						Logger.Warn($"HttpJobDispatcher.GetJobItem:: get data from DashbordContext err,Request:{request.GetType().FullName}");
						return new Tuple<T, string>(default(T), $"HttpJobDispatcher.GetJobItem:: get data from DashbordContext err,Request:{request.GetType().FullName}");
					}
				}

				if (body == null)
				{
					return new Tuple<T, string>(default(T), $"get body stream from request fail");
				}

				using (MemoryStream ms = new MemoryStream())
				{
					await body.CopyToAsync(ms);
					await ms.FlushAsync();
					ms.Seek(0, SeekOrigin.Begin);
					var sr = new StreamReader(ms);
					var requestBody = await sr.ReadToEndAsync();
					if (typeof(T) == typeof(String))
					{
						return new Tuple<T, string>((T)(object)requestBody, null);
					}
					return new Tuple<T, string>(Newtonsoft.Json.JsonConvert.DeserializeObject<T>(requestBody), null);
				}
			}
			catch (Exception ex)
			{
				return new Tuple<T, string>(default(T), ex.Message);
			}
		}


		/// <summary>
		/// 添加后台作业
		/// </summary>
		/// <param name="jobItem"></param>
		/// <returns></returns>
		public string AddHttpbackgroundjob(HttpJobItem jobItem)
		{
			try
			{
				if (string.IsNullOrEmpty(jobItem.QueueName))
				{
					jobItem.QueueName = EnqueuedState.DefaultQueue;
				}
				else
				{
					//get queues from server
					// ReSharper disable once ReplaceWithSingleCallToFirstOrDefault
					var server = JobStorage.Current.GetMonitoringApi().Servers().Where(p => p.Queues.Count > 0).FirstOrDefault();
					// ReSharper disable once PossibleNullReferenceException
					if (server == null)
					{
						return "active server not exist!";
					}
					var queues = server.Queues.Select(m => m.ToLower()).ToList();
					if (!queues.Exists(p => p == jobItem.QueueName.ToLower()) || queues.Count == 0)
					{
						Logger.Warn($"HttpJobDispatcher.AddHttpbackgroundjob Error => HttpJobItem.QueueName：`{jobItem.QueueName}` not exist, Use DEFAULT extend!");
						jobItem.QueueName = EnqueuedState.DefaultQueue;
					}
				}

				if (!string.IsNullOrEmpty(jobItem.RunAt))
				{
					//如果设置了 指定的运行时间 先parse一下
					if (DateTimeOffset.TryParse(jobItem.RunAt, out var runAtTime))
					{
						return BackgroundJob.Schedule(() => HttpJob.Excute(jobItem, null, null, false, null), runAtTime);
					}
				}

				if (jobItem.DelayFromMinutes <= 0)
				{
					return BackgroundJob.Enqueue(() => HttpJob.Excute(jobItem, null, null, false, null));
				}

				return BackgroundJob.Schedule(() => HttpJob.Excute(jobItem, null, null, false, null),
					TimeSpan.FromMinutes(jobItem.DelayFromMinutes));
			}
			catch (Exception ex)
			{
				Logger.ErrorException("HttpJobDispatcher.AddHttpbackgroundjob", ex);
				return null;
			}
		}

		/// <summary>
		/// 执行job
		/// </summary>
		/// <param name="context"></param>
		/// <returns></returns>
		public async Task StartBackgroudJob(DashboardContext context)
		{
			try
			{
				var jobItemBody = await GetRequestBody<HttpJobItem>(context);
				var jobItem = jobItemBody.Item1;
				if (jobItem == null || string.IsNullOrEmpty(jobItem.JobName))
				{
					context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
					await context.Response.WriteAsync($"invalid request body:{jobItemBody.Item2}");
					return;
				}

				if (string.IsNullOrEmpty(jobItem.Data))
				{
					context.Response.StatusCode = (int)HttpStatusCode.NoContent;
					return;
				}

				using (var connection = JobStorage.Current.GetConnection())
				{
					var hashKey = CodingUtil.MD5((!string.IsNullOrEmpty(jobItem.RecurringJobIdentifier) ? jobItem.RecurringJobIdentifier : jobItem.JobName) + ".runtime");
					using (var tran = connection.CreateWriteTransaction())
					{
						tran.SetRangeInHash(hashKey, new List<KeyValuePair<string, string>>
						{
							new KeyValuePair<string, string>("Data", jobItem.Data)
						});
						tran.Commit();
					}
				}

				context.Response.StatusCode = (int)HttpStatusCode.NoContent;
				return;
			}
			catch (Exception ex)
			{
				Logger.ErrorException("HttpJobDispatcher.StartBackgroudJob", ex);
				context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
				await context.Response.WriteAsync(ex.Message);
			}
		}

		/// <summary>
		/// 发出jobAgent停止命令
		/// </summary>
		/// <param name="context"></param>
		/// <returns></returns>
		public async Task StopBackgroudJob(DashboardContext context)
		{
			try
			{

				var jobItemBody = await GetRequestBody<HttpJobItem>(context);
				var jobItem = jobItemBody.Item1;
				if (jobItem == null || string.IsNullOrEmpty(jobItem.JobName))
				{
					context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
					await context.Response.WriteAsync($"invalid request body:{jobItemBody.Item2}");
					return;
				}

				using (var connection = JobStorage.Current.GetConnection())
				{
					var hashKey = CodingUtil.MD5(jobItem.JobName + ".runtime");
					using (var tran = connection.CreateWriteTransaction())
					{
						tran.SetRangeInHash(hashKey, new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("Action", "stop") });
						tran.Commit();
					}
				}

				context.Response.StatusCode = (int)HttpStatusCode.NoContent;
				return;
			}
			catch (Exception ex)
			{
				Logger.ErrorException("HttpJobDispatcher.StopBackgroudJob", ex);
				context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
				await context.Response.WriteAsync(ex.Message);
			}
		}

		/// <summary>
		/// 获取已暂停job配置的cron
		/// </summary>
		/// <param name="jobname"></param>
		/// <returns></returns>
		private string getPauseJobCronText(string jobname)
		{
			using (var connection = JobStorage.Current.GetConnection())
			{
				//拿到所有的设置
				var conts = connection.GetAllItemsFromSet($"JobPauseOf:{jobname}");
				var cron = conts.FirstOrDefault(r => r.StartsWith("Cron:"));
				if (!string.IsNullOrEmpty(cron)) cron = cron.Replace("Cron:", "");
				return cron;
			}
		}
		
		/// <summary>
		/// 停止或者暂停项目
		/// </summary>
		/// <param name="jobname"></param>
		/// <param name="existJob"></param>
		/// <returns></returns>
		public string PauseOrRestartJob(string jobname, HttpJobItem existJob = null)
		{
			try
			{
				using (var connection = JobStorage.Current.GetConnection())
				{
					var job = existJob;
					if (job == null)
					{
						Dictionary<string, string> dictionary = connection.GetAllEntriesFromHash("recurring-job:" + jobname);
						if (dictionary == null || dictionary.Count == 0)
						{
							return "not found recurring-job:" + jobname;
						}

						if (!dictionary.TryGetValue(nameof(Job), out var jobDetail))
						{
							return "not found recurring-job:" + jobname;
						}

						var RecurringJob = InvocationData.DeserializePayload(jobDetail).DeserializeJob();

						job = CodingUtil.FromJson<HttpJobItem>(RecurringJob.Args.FirstOrDefault()?.ToString());

						if (job == null) return "fail parse recurring-job:" + jobname;
					}

					using (var tran = connection.CreateWriteTransaction())
					{
						//拿到所有的设置
						var conts = connection.GetAllItemsFromSet($"JobPauseOf:{jobname}");

						//有就先清掉
						foreach (var pair in conts)
						{
							tran.RemoveFromSet($"JobPauseOf:{jobname}", pair);
						}

						if (existJob != null)
						{
							// 暂停情况走进来的 只更新cron
							tran.AddToSet($"JobPauseOf:{jobname}", "true");
							tran.AddToSet($"JobPauseOf:{jobname}", "Cron:" + job.Cron);
						}
						else
						{
							var cron = conts.FirstOrDefault(r => r.StartsWith("Cron:"));
							if (!string.IsNullOrEmpty(cron)) cron = cron.Replace("Cron:", "");
							//如果包含有true 的 说明已经设置了 暂停 要把改成 启动 并且拿到 Cron 进行更新
							if (conts.Contains("true"))
							{
								tran.AddToSet($"JobPauseOf:{jobname}", "false");
								if (!string.IsNullOrEmpty(cron))
								{
									job.Cron = cron;
									AddHttprecurringjob(job);
								}
							}
							else
							{
								tran.AddToSet($"JobPauseOf:{jobname}", "true");
								tran.AddToSet($"JobPauseOf:{jobname}", "Cron:" + job.Cron);
								job.Cron = ""; // 暂停
								AddHttprecurringjob(job);
							}
						}
						tran.Commit();
					}
				}

				return string.Empty;
			}
			catch (Exception ex)
			{
				Logger.ErrorException("HttpJobDispatcher.PauseOrRestartJob", ex);
				return ex.Message;
			}
		}

		/// <summary>
		/// 获取已暂停job原来的cron表达式
		/// </summary>
		/// <param name="context"></param>
		private async Task GetPauseJobCron(DashboardContext context)
		{
			var jobItemBody = await GetRequestBody<HttpJobItem>(context);
			var jobItem = jobItemBody.Item1;
			if (jobItem == null || string.IsNullOrEmpty(jobItem.JobName))
			{
				context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
				await context.Response.WriteAsync($"invalid request body:{jobItemBody.Item2}");
				return;
			}

			var cron = getPauseJobCronText(jobItem.JobName);
			if (string.IsNullOrEmpty(cron))
			{
				cron = Cron.Never();
			}
			context.Response.StatusCode = (int)HttpStatusCode.OK;
			await context.Response.WriteAsync(cron);
		}

		/// <summary>
		/// 暂停或开始job
		/// </summary>
		/// <param name="context"></param>
		/// <returns></returns>
		private async Task DoPauseOrRestartJob(DashboardContext context)
		{
			var jobItemBody = await GetRequestBody<HttpJobItem>(context);
			var jobItem = jobItemBody.Item1;
			if (jobItem == null || string.IsNullOrEmpty(jobItem.JobName))
			{
				context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
				await context.Response.WriteAsync($"invalid request body:{jobItemBody.Item2}");
				return;
			}

			var result = PauseOrRestartJob(jobItem.JobName);

			if (!string.IsNullOrEmpty(result))
			{
				context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
				await context.Response.WriteAsync(result);
				return;
			}

			context.Response.StatusCode = (int)HttpStatusCode.NoContent;
		}

		/// <summary>
		/// 删除周期性job
		/// </summary>
		/// <param name="context"></param>
		/// <returns></returns>
		private async Task DelJob(DashboardContext context)
		{
			try
			{
				var jobItemBody = await GetRequestBody<HttpJobItem>(context);
				var jobItem = jobItemBody.Item1;
				if (jobItem == null || string.IsNullOrEmpty(jobItem.JobName))
				{
					context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
					await context.Response.WriteAsync($"invalid request body:{jobItemBody.Item2}");
					return;
				}

				if (!string.IsNullOrEmpty(jobItem.Data) && jobItem.Data == "backgroundjob")
				{
					//删除backgroundjob
					var result = BackgroundJob.Delete(jobItem.JobName);
					if (!result)
					{
						context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
						await context.Response.WriteAsync($"remove:{jobItem.JobName} fail");
						return;
					}

					context.Response.StatusCode = (int)HttpStatusCode.NoContent;
					return;
				}

				//删除周期性job
				RecurringJob.RemoveIfExists(jobItem.JobName);
				context.Response.StatusCode = (int)HttpStatusCode.NoContent;
			}
			catch (Exception ex)
			{
				context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
				await context.Response.WriteAsync(ex.Message);
				return;
			}
		}

		/// <summary>
		/// 添加周期性作业
		/// </summary>
		/// <returns></returns>
		public string AddHttprecurringjob(HttpJobItem jobItem, bool addOnly = false)
		{
			if (string.IsNullOrEmpty(jobItem.QueueName))
			{
				jobItem.QueueName = EnqueuedState.DefaultQueue;
			}
			else
			{
				//get queues from server
				// ReSharper disable once ReplaceWithSingleCallToFirstOrDefault
				var server = JobStorage.Current.GetMonitoringApi().Servers().Where(p => p.Queues.Count > 0).FirstOrDefault();
				// ReSharper disable once PossibleNullReferenceException
				if (server == null)
				{
					return "active server not exist!";
				}
				var queues = server.Queues.Select(m => m.ToLower()).ToList();
				if (!queues.Exists(p => p == jobItem.QueueName.ToLower()) || queues.Count == 0)
				{
					Logger.Warn($"HttpJobDispatcher.AddHttprecurringjob Error => HttpJobItem.QueueName：`{jobItem.QueueName}` not exist, Use DEFAULT extend!");
					jobItem.QueueName = EnqueuedState.DefaultQueue;
				}
			}

			try
			{

				// 先用每个job配置的 如果没有就用系统配置的 在没有就用Local
				TimeZoneInfo timeZone = null;
				if (!string.IsNullOrEmpty(jobItem.TimeZone))
				{
					timeZone = TimeZoneInfoHelper.OlsonTimeZoneToTimeZoneInfo(jobItem.TimeZone);
				}

				//https://github.com/yuzd/Hangfire.HttpJob/issues/78
				var jobidentifier = jobItem.getJobIdentifier();
				if (addOnly)
				{
					using (var connection = JobStorage.Current.GetConnection())
					{
						var existItem = connection.GetAllEntriesFromHash("recurring-job:" + jobidentifier);
						if (existItem != null && existItem.Count > 0)
						{
							return jobidentifier + "is registerd!";
						}
					}
				}

				if (timeZone == null) timeZone = TimeZoneInfo.Local;
				if (string.IsNullOrEmpty(jobItem.Cron))
				{
					//支持添加一个 只能手动触发的
					RecurringJob.AddOrUpdate(jobidentifier, () => HttpJob.Excute(jobItem, null, null, false, null), Cron.Never,
						new RecurringJobOptions
						{
							TimeZone = timeZone, 
#pragma warning disable CS0618
							QueueName = jobItem.QueueName.ToLower()
#pragma warning restore CS0618
						});
					return string.Empty;
				}
				RecurringJob.AddOrUpdate(jobidentifier, () => HttpJob.Excute(jobItem, null, null, false, null), jobItem.Cron,
					new RecurringJobOptions
					{
						TimeZone = timeZone, 
#pragma warning disable CS0618
						QueueName = jobItem.QueueName.ToLower()
#pragma warning restore CS0618
					});
				return string.Empty;
			}
			catch (Exception ex)
			{
				Logger.ErrorException("HttpJobDispatcher.AddHttprecurringjob", ex);
				return ex.Message;
			}
		}

		/// <summary>
		/// 获取job任务
		/// </summary>
		/// <param name="name"></param>
		/// <returns></returns>
		public string GetRecurringJobString(string name)
		{
			try
			{
				return JsonConvert.SerializeObject(JsonConvert.DeserializeObject<RecurringJobItem>(GetRecurringJob(name).ToString()));
			}
			catch (Exception ex)
			{
				Logger.ErrorException("HttpJobDispatcher.GetRecurringJobString", ex);
			}

			return "";
		}

		/// <summary>
		/// 获取job任务
		/// </summary>
		/// <param name="name"></param>
		/// <returns></returns>
		public HttpJobItem GetRecurringJob(string name)
		{
			try
			{
				using (var connection = JobStorage.Current.GetConnection())
				{
					Dictionary<string, string> dictionary = connection.GetAllEntriesFromHash("recurring-job:" + name);
					if (dictionary == null || dictionary.Count == 0)
					{
						return default;
					}

					if (!dictionary.TryGetValue(nameof(Job), out var jobDetail))
					{
						return default;
					}

					var RecurringJob = InvocationData.DeserializePayload(jobDetail).DeserializeJob();
					var jobItem = RecurringJob.Args.FirstOrDefault() as HttpJobItem;
					if (jobItem == null) return default;
					if (string.IsNullOrEmpty(jobItem.RecurringJobIdentifier))
					{
						jobItem.RecurringJobIdentifier = jobItem.JobName;
					}

					return jobItem;
				}
			}
			catch (Exception ex)
			{
				Logger.ErrorException("HttpJobDispatcher.GetRecurringJob", ex);
			}

			return default;
		}

		/// <summary>
		///  判断job是否已被暂停
		/// </summary>
		/// <param name="jobname"></param>
		/// <returns></returns>
		public bool IsJobPaused(string jobname)
		{
			using (var connection = JobStorage.Current.GetConnection())
			{
				Dictionary<string, string> dictionary = connection.GetAllEntriesFromHash("recurring-job:" + jobname);
				if (dictionary == null || dictionary.Count == 0)
				{
					return false;
				}

				if (!dictionary.TryGetValue(nameof(Job), out var jobDetail))
				{
					return false;
				}

				var RecurringJob = InvocationData.DeserializePayload(jobDetail).DeserializeJob();

				var job = CodingUtil.FromJson<HttpJobItem>(RecurringJob.Args.FirstOrDefault()?.ToString());
				if (job == null) return false;

				//拿到所有的设置
				var conts = connection.GetAllItemsFromSet($"JobPauseOf:{jobname}");
				return conts.Contains("true");
			}
		}

		/// <summary>
		/// 获取jobAgent类型的JobInfo
		/// </summary>
		/// <param name="context"></param>
		/// <returns></returns>
		private async Task DoGetBackGroundJobDetail(DashboardContext context)
		{
			var jobDetail = await GetBackGroundJobDetail(context);
			context.Response.ContentType = "application/json";
			context.Response.StatusCode = (int)HttpStatusCode.OK;
			await context.Response.WriteAsync(JsonConvert.SerializeObject(jobDetail));
		}

		/// <summary>
		/// 获取jobAgent类型的JobInfo
		/// </summary>
		/// <param name="context"></param>
		/// <returns></returns>
		private async Task<JobDetailInfo> GetBackGroundJobDetail(DashboardContext context)
		{
			var result = new JobDetailInfo();
			var jobName = string.Empty;

			var jobItemBody = await GetRequestBody<HttpJobItem>(context);
			var jobItem = jobItemBody.Item1;
			if (jobItem == null || string.IsNullOrEmpty(jobItem.JobName))
			{
				result.Info = "GetJobDetail Error：can not found job by id:" + jobItemBody.Item2;
				return result;
			}

			try
			{
				using (var connection = JobStorage.Current.GetConnection())
				{
					Job job = null;

					if (string.IsNullOrEmpty(jobItem.Cron))
					{
						var jobData = connection.GetJobData(jobItem.JobName);
						if (jobData == null)
						{
							result.Info = "GetJobDetail Error：can not found job by id:" + jobItem.JobName;
							return result;
						}

						job = jobData.Job;
					}
					else
					{
						Dictionary<string, string> dictionary = connection.GetAllEntriesFromHash("recurring-job:" + jobItem.JobName);
						if (dictionary == null || dictionary.Count == 0)
						{
							result.Info = "GetJobDetail Error：can not found job by id:" + jobItem.JobName;
							return result;
						}

						if (!dictionary.TryGetValue(nameof(Job), out var jobDetail))
						{
							result.Info = "GetJobDetail Error：can not found job by id:" + jobItem.JobName;
							return result;
						}

						job = InvocationData.DeserializePayload(jobDetail).DeserializeJob();
					}

					var jobItem2 = job.Args.FirstOrDefault();
					var httpJobItem = jobItem2 as HttpJobItem;
					if (httpJobItem == null)
					{
						result.Info = $"GetJobDetail Error：jobData can not found job by id:" + jobItem.JobName;
						return result;
					}

					result.JobName = jobName = httpJobItem.JobName;
					if (string.IsNullOrEmpty(httpJobItem.AgentClass))
					{
						result.Info = $"{(!string.IsNullOrEmpty(jobName) ? "【" + jobName + "】" : string.Empty)} Error：is not AgentJob! ";
						return result;
					}

					var jobInfo = HttpJob.GetAgentJobDetail(httpJobItem);
					if (string.IsNullOrEmpty(jobInfo))
					{
						result.Info = $"{(!string.IsNullOrEmpty(jobName) ? "【" + jobName + "】" : string.Empty)} Error：get null info! ";
						return result;
					}

					jobInfo = jobInfo.Replace("\r\n", "<br/>");
					result.Info = jobInfo;
					return result;
				}
			}
			catch (Exception ex)
			{
				Logger.ErrorException("HttpJobDispatcher.GetBackGroundJobDetail", ex);
				result.Info = $"{(!string.IsNullOrEmpty(jobName) ? "【" + jobName + "】" : string.Empty)} GetJobDetail Error：" + ex.ToString();
				return result;
			}
		}


		/// <summary>
		/// 导出所有的任务
		/// </summary>
		/// <param name="context"></param>
		/// <returns></returns>
		private async Task ExportJobsAsync(DashboardContext context)
		{
			try
			{
				var jobList = GetAllRecurringJobs();
				var jobItems = jobList.Select(m => m.Job.Args[0]).ToList();
				context.Response.ContentType = "application/json";
				context.Response.StatusCode = (int)HttpStatusCode.OK;
				await context.Response.WriteAsync(JsonConvert.SerializeObject(jobItems));
			}
			catch (Exception e)
			{
				await context.Response.WriteAsync("err:" + e.Message);
			}
		}

		/// <summary>
		/// 分页获取任务
		/// </summary>
		/// <param name="context"></param>
		/// <returns></returns>
		private async Task RecurringJobsAsync(DashboardContext context)
		{
			try
			{
				var contentBody = await GetRequestBody<PageDto>(context);
				var jobList = GetAllRecurringJobs(contentBody.Item1.PageNo, contentBody.Item1.PageSize);
				context.Response.ContentType = "application/json";
				context.Response.StatusCode = (int)HttpStatusCode.OK;
				await context.Response.WriteAsync(JsonConvert.SerializeObject(jobList));
			}
			catch (Exception e)
			{
				await context.Response.WriteAsync("err:" + e.Message);
			}
		}

		/// <summary>
		/// 导入所有的任务
		/// </summary>
		/// <param name="context"></param>
		/// <returns></returns>
		private async Task ImportJobsAsync(DashboardContext context)
		{
			try
			{
				var contentBody = await GetRequestBody<string>(context);
				if (string.IsNullOrEmpty(contentBody.Item1))
				{
					await context.Response.WriteAsync($"err: json invaild:{contentBody.Item2}");
					return;
				}
				var jobItems = JsonConvert.DeserializeObject<List<HttpJobItem>>(contentBody.Item1);
				foreach (var jobItem in jobItems)
				{
					AddHttprecurringjob(jobItem);
				}
				context.Response.ContentType = "application/json";
				context.Response.StatusCode = (int)HttpStatusCode.OK;
				await context.Response.WriteAsync(JsonConvert.SerializeObject(jobItems));
			}
			catch (Exception e)
			{
				await context.Response.WriteAsync("err:" + e.Message);
			}
		}

		/// <summary>
		/// 序列化jsonstring
		/// </summary>
		/// <param name="str"></param>
		/// <returns></returns>
		private string ConvertJsonString(string str)
		{
			try
			{
				//格式化json字符串
				JsonSerializer serializer = new JsonSerializer();
				TextReader tr = new StringReader(str);
				JsonTextReader jtr = new JsonTextReader(tr);
				object obj = serializer.Deserialize(jtr);
				if (obj != null)
				{
					StringWriter textWriter = new StringWriter();
					JsonTextWriter jsonWriter = new JsonTextWriter(textWriter)
					{
						Formatting = Formatting.Indented,
						Indentation = 4,
						IndentChar = ' '
					};
					serializer.Serialize(jsonWriter, obj);
					return textWriter.ToString();
				}
				else
				{
					return str;
				}
			}
			catch (Exception)
			{
				return string.Empty;
			}
		}


		private List<RecurringJobDto> GetAllRecurringJobs()
		{
			var jobList = new List<RecurringJobDto>();
			try
			{
				using (var connection = JobStorage.Current.GetConnection())
				{
					jobList = connection.GetRecurringJobs();

				}
			}
			catch (Exception ex)
			{
				Logger.ErrorException("HttpJobDispatcher.GetAllRecurringJobs", ex);
			}
			return jobList;
		}

		private object GetAllRecurringJobs(int pageNo, int pageSize)
		{
			var jobList = new List<RecurringJobDto>();
			try
			{
				using (var connection = JobStorage.Current.GetConnection())
				{
					if (connection is JobStorageConnection storageConnection)
					{
						var pager = new Pager((pageNo - 1) * pageSize, pageSize, storageConnection.GetRecurringJobCount());
						jobList = storageConnection.GetRecurringJobs(pager.FromRecord, pager.FromRecord + pager.RecordsPerPage - 1);

						return new
						{
							pageNo = pageNo,
							pageSize = pageSize,
							rows = jobList.Select(m => m.Job.Args[0]).ToList(),
							totalPage = pager.TotalPageCount,
							totalRows = pager.TotalRecordCount
						};
					}
					jobList = connection.GetRecurringJobs();
				}
			}
			catch (Exception ex)
			{
				Logger.ErrorException("HttpJobDispatcher.GetAllRecurringJobs", ex);
			}
			return jobList;
		}
	}
}