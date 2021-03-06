﻿using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Hangfire.Common;
using Hangfire.MySql.Common;
using Hangfire.MySql.src.Entities;
using Hangfire.MySql.src.Entities.Extensions;
using Hangfire.MySql.src.Entities.Filters;
using Hangfire.States;
using Hangfire.Storage;
using Hangfire.Storage.Monitoring;
using LinqToDB;
using MySql.Data.MySqlClient;
using Newtonsoft.Json;
using Job = Hangfire.Common.Job;

namespace Hangfire.MySql.src
{
    internal class MySqlMonitoringApi : ShortConnectingDatabaseActor, IMonitoringApi
    {
        private readonly PersistentJobQueueProviderCollection _queueProviders;

        public MySqlMonitoringApi(
            string connectionString,
            PersistentJobQueueProviderCollection queueProviders)
            : base(connectionString)
        {
            _queueProviders = queueProviders;
        }

        public IList<QueueWithTopEnqueuedJobsDto> Queues()
        {
            var s = _queueProviders
                        .Select(qProvider => qProvider.GetJobQueueMonitoringApi(ConnectionString))
                        .SelectMany(x => x.GetQueues())
                        .ToList();


            const int MaxFirstJobs = 5;

            var result = new List<QueueWithTopEnqueuedJobsDto>();
            foreach (var queue in s)
            {

                var monitoring = _queueProviders.GetProvider(queue).GetJobQueueMonitoringApi(ConnectionString);


                var qw = new QueueWithTopEnqueuedJobsDto()
                {
                    Fetched = 0,
                    FirstJobs = EnqueuedJobs(queue, 0, MaxFirstJobs),
                    Length = monitoring.GetEnqueuedAndFetchedCount(queue).EnqueuedCount.Value,
                    Name = queue
                };


                result.Add(qw);


            }

            return result;
            //return new List<QueueWithTopEnqueuedJobsDto>();
        }

        public IList<ServerDto> Servers()
        {
            return UsingTable<Entities.Server, IList<ServerDto>>(servers =>
            {

                var result = new List<ServerDto>();

                foreach (var server in servers)
                {
                    var data = JobHelper.FromJson<ServerData>(server.Data);

                    result.Add(new ServerDto
                    {
                        Name = server.Id,
                        Heartbeat = server.LastHeartbeat,
                        Queues = data.Queues,
                        StartedAt = data.StartedAt.HasValue ? data.StartedAt.Value : DateTime.MinValue,
                        WorkersCount = data.WorkerCount
                    });

                }

                Debug.WriteLine("MySqlMonitoringApi  Servers() returning " + result.Count);

                return result.ToList();

            });

        }

        private static Job DeserializeJob(string invocationData, string arguments)
        {
            var data = JobHelper.FromJson<InvocationData>(invocationData);
            data.Arguments = arguments;

            try
            {
                return data.Deserialize();
            }
            catch (JobLoadException)
            {
                return null;
            }
        }

        public JobDetailsDto JobDetails(string jobId)
        {
            return UsingDatabase<JobDetailsDto>(db =>
            {
                var job = db.GetTable<Entities.Job>().SingleById(jobId);

                var histories = db.GetTable<Entities.JobState>().Where(js => js.JobId == job.Id).Select(jobState => new StateHistoryDto()
                {
                    CreatedAt = jobState.CreatedAt,
                    Reason = jobState.Reason,
                    StateName = jobState.Name,
                    Data = JsonConvert.DeserializeObject<Dictionary<string, string>>(jobState.Data)
                }).ToList();


                var jobDetailsDto = new JobDetailsDto()
                {
                    CreatedAt = job.CreatedAt,
                    ExpireAt = job.ExpireAt,
                    Properties = db.GetTable<Entities.JobParameter>().Where(jp=>jp.JobId==job.Id).ToDictionary(jp => jp.Name, jp => jp.Value),
                    History = histories
                };

                return jobDetailsDto;

            });

        }

        protected long GetCounterTotal(string key)
        {
            return UsingTable<Counter, long>(counters => counters.Where(c => c.Key == key).Sum(c=>c.Value));
        }

        protected long GetNJobsInState(string stateName)
        {
            return UsingTable<Entities.Job, long>(jobs => jobs.Count(j => j.StateName == stateName));
        }

        public StatisticsDto GetStatistics()
        {
            return new StatisticsDto()
            {
                Deleted = GetCounterTotal("stats:deleted"),
                Enqueued = GetNJobsInState(EnqueuedState.StateName),
                Failed = GetNJobsInState(FailedState.StateName),
                Processing = GetNJobsInState(ProcessingState.StateName),
                Queues = 0,
                /* _queueProviders
                    .SelectMany(x => x.GetJobQueueMonitoringApi(connection).GetQueues())
                    .Count()
                 * */
                Recurring = UsingTable<Entities.Set, long>(sets => sets.Count(s => s.Key == "recurring-jobs")),
                Succeeded = GetCounterTotal("stats:succeeded"),
                Scheduled = GetNJobsInState(ScheduledState.StateName),
                Servers = Servers().Count
            };
        }

        public JobList<EnqueuedJobDto> EnqueuedJobs(string queue, int @from, int perPage)
        {

            var jobQueues = UsingTable<Entities.JobQueue, IEnumerable<Entities.JobQueue>>(
                jQs => jQs
                    .InQueue(queue)
                    .OrderBy(jQ => jQ.Id)
                    .Skip(from)
                    .Take(perPage)
                    .ToArray());


            var r = new List<KeyValuePair<string, EnqueuedJobDto>>();
            foreach (var jQ in jobQueues)
            {
                var job = UsingTable<Entities.Job, Entities.Job>(jobs => jobs.Single(j => j.Id == jQ.JobId));

                var e = new EnqueuedJobDto()
                {
                    EnqueuedAt =
                        job.IsInState(EnqueuedState.StateName)
                            ? job.GetNullableDateTimeStateDataValue("EnqueuedAt")
                            : null,
                    InEnqueuedState = job.IsInState(EnqueuedState.StateName),
                    Job = job.ToJobData().Job,
                    State = job.StateName
                };

                var pair = new KeyValuePair<string, EnqueuedJobDto>(job.Id.ToString(CultureInfo.InvariantCulture), e);
                r.Add(pair);


            }
        
            return new JobList<EnqueuedJobDto>(r);
            //return new JobList<EnqueuedJobDto>(new List<KeyValuePair<string, EnqueuedJobDto>>());
        }

        public JobList<FetchedJobDto> FetchedJobs(string queue, int @from, int perPage)
        {
            return new JobList<FetchedJobDto>(new List<KeyValuePair<string, FetchedJobDto>>());
        }

        public JobList<ProcessingJobDto> ProcessingJobs(int @from, int count)
        {
            return new JobList<ProcessingJobDto>(new List<KeyValuePair<string, ProcessingJobDto>>());
        }

        public JobList<ScheduledJobDto> ScheduledJobs(int @from, int count)
        {
            return new JobList<ScheduledJobDto>(new List<KeyValuePair<string, ScheduledJobDto>>());
        }

        public JobList<SucceededJobDto> SucceededJobs(int @from, int count)
        {
            return UsingDatabase(db =>
            {

                var jobs = db.GetTable<Entities.Job>()
                    .Where(j=>j.StateName=="Succeeded")
                    .OrderByDescending(j=>j.Id)
                    .Skip(from)
                    .Take(count);

                var list = new List<KeyValuePair<string, SucceededJobDto>>();

                foreach (var sqlJob in jobs)
                {

                    var stateData = sqlJob.ToStateData().Data;

                    var s = new SucceededJobDto()
                    {
                        Job = sqlJob.ToCommonJob(),
                        InSucceededState = true,
                        Result = stateData.ContainsKey("Result") ? stateData["Result"] : null,
                        TotalDuration = stateData.ContainsKey("PerformanceDuration") && stateData.ContainsKey("Latency")
                            ? (long?) long.Parse(stateData["PerformanceDuration"]) +
                              (long?) long.Parse(stateData["Latency"])
                            : null,
                        SucceededAt = sqlJob.GetNullableDateTimeStateDataValue("SucceededAt")
                    };

                    list.Add(new KeyValuePair<string, SucceededJobDto>(
                        sqlJob.Id.ToString(CultureInfo.InvariantCulture), s));

                }

                return new JobList<SucceededJobDto>(list);

            });

        }


        public JobList<FailedJobDto> FailedJobs(int @from, int count)
        {
            return new JobList<FailedJobDto>(new List<KeyValuePair<string, FailedJobDto>>());


        }

        public JobList<DeletedJobDto> DeletedJobs(int @from, int count)
        {
            return new JobList<DeletedJobDto>(new List<KeyValuePair<string, DeletedJobDto>>());

        }

        public long ScheduledCount()
        {
            return GetNJobsInState(ScheduledState.StateName);
        }

        public long EnqueuedCount(string queue)
        {
            return GetQueueApi(queue).GetEnqueuedAndFetchedCount(queue).EnqueuedCount.Value;
        }

        public long FetchedCount(string queue)
        {
            return GetQueueApi(queue).GetEnqueuedAndFetchedCount(queue).FetchedCount.Value;
        }

        public long FailedCount()
        {
            return GetNJobsInState(FailedState.StateName);
        }

        public long ProcessingCount()
        {
            return GetNJobsInState(ProcessingState.StateName);
        }

        public long SucceededListCount()
        {
            return GetNJobsInState(SucceededState.StateName);
        }

        public long DeletedListCount()
        {
            return GetNJobsInState(DeletedState.StateName);
        }

        public IDictionary<DateTime, long> SucceededByDatesCount()
        {
            return new Dictionary<DateTime, long>();
        }

        public IDictionary<DateTime, long> FailedByDatesCount()
        {
            return new Dictionary<DateTime, long>();
        }

        public IDictionary<DateTime, long> HourlySucceededJobs()
        {
            return new Dictionary<DateTime, long>();
        }

        public IDictionary<DateTime, long> HourlyFailedJobs()
        {
            return new Dictionary<DateTime, long>();
        }

        private IPersistentJobQueueMonitoringApi GetQueueApi(string queueName)
        {
            var provider = _queueProviders.GetProvider(queueName);
            var monitoringApi = provider.GetJobQueueMonitoringApi(ConnectionString);
            return monitoringApi;
        }


    }
}
