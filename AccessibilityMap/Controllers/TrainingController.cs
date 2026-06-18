namespace KyivAccessibilityMap.Controllers
{
    using Microsoft.AspNetCore.Mvc;
    using Google.Cloud.AIPlatform.V1;
    using Google.Apis.Auth.OAuth2;
    using Google.Cloud.Storage.V1;
    using System.Text.Json;

    [ApiController]
    [Route("api/training")]
    public class TrainingController : ControllerBase
    {
        private readonly IWebHostEnvironment _env;
        private readonly IConfiguration _config;

        private string ProjectId    => _config["GoogleCloud:ProjectId"] ?? "";
        private string BucketName   => _config["GoogleCloud:BucketName"] ?? "";
        private string KeyFilePath  => Path.Combine(
            Directory.GetCurrentDirectory(),
            _config["GoogleCloud:KeyFilePath"] ?? "Keys/gcloud-key.json");
        private const string Region = "europe-west1";

        // Список класифікаторів
        private static readonly List<ClassifierInfo> Classifiers = new()
        {
            new("ramp",            "ramp_classification",     "ramp"),
            new("lit",             "lit_classification",      "lit"),
            new("smoothness",      "smooth_classification",   "smoothness"),
            new("width",           "width_classification",    "width"),
            new("tactile",         "tactile_classification",  "tactile"),
            new("surface_quality", "quality_classification",  "surface_quality"),
            new("surface_type",    "surface_classification",  "surface_type"),
        };

        public TrainingController(IWebHostEnvironment env, IConfiguration config)
        {
            _env = env;
            _config = config;
        }

        // ── Отримати credentials: спершу з env-змінної (Render), потім з файлу (локально) ──
        private GoogleCredential GetCredential()
        {
            var json = Environment.GetEnvironmentVariable("GCLOUD_KEY_JSON");
            if (!string.IsNullOrEmpty(json))
            {
                return GoogleCredential.FromJson(json);
            }
            return GoogleCredential.FromFile(KeyFilePath);
        }

        // ── Список класифікаторів і статус моделей ────────────────────────────
        [HttpGet("classifiers")]
        public async Task<ActionResult<object>> GetClassifiers()
        {
            try
            {
                var storageClient = await CreateStorageClient();
                var result = new List<object>();

                foreach (var clf in Classifiers)
                {
                    // Перевіряємо чи є готова модель в GCS
                    bool modelExists = false;
                    string? modelPath = null;

                    try
                    {
                        var objectName = $"models/{clf.ModelName}_best.pth";
                        await storageClient.GetObjectAsync(BucketName, objectName);
                        modelExists = true;
                        modelPath = $"gs://{BucketName}/{objectName}";
                    }
                    catch { }

                    // Перевіряємо чи є локальна модель
                    var localPath = Path.Combine(_env.WebRootPath, "models", $"{clf.ModelName}_best.pth");
                    bool localExists = System.IO.File.Exists(localPath);

                    result.Add(new
                    {
                        id = clf.Id,
                        name = clf.DisplayName,
                        modelName = clf.ModelName,
                        datasetFolder = clf.DatasetFolder,
                        modelInGcs = modelExists,
                        modelPath,
                        modelLocal = localExists
                    });
                }

                return Ok(result);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = ex.Message });
            }
        }

        // ── Запустити навчання одного класифікатора ───────────────────────────
        [HttpPost("start/{classifierId}")]
        public async Task<ActionResult<object>> StartTraining(string classifierId)
        {
            try
            {
                var clf = Classifiers.FirstOrDefault(c => c.Id == classifierId);
                if (clf == null)
                    return NotFound(new { message = $"Класифікатор '{classifierId}' не знайдено" });

                var credential = GetCredential()
                    .CreateScoped("https://www.googleapis.com/auth/cloud-platform");

                var clientBuilder = new JobServiceClientBuilder
                {
                    Endpoint = $"{Region}-aiplatform.googleapis.com",
                    GoogleCredential = credential
                };
                var jobClient = await clientBuilder.BuildAsync();

                var jobName = $"train-{clf.Id}-{DateTime.UtcNow:yyyyMMdd-HHmmss}";

                var customJob = new CustomJob
                {
                    DisplayName = jobName,
                    JobSpec = new CustomJobSpec
                    {
                        WorkerPoolSpecs =
                        {
                            new WorkerPoolSpec
                            {
                                MachineSpec = new MachineSpec
                                {
                                    MachineType = "n1-standard-4",
                                },
                                ReplicaCount = 1,
                                ContainerSpec = new ContainerSpec
                                {
                                    ImageUri = "us-docker.pkg.dev/vertex-ai/training/pytorch-xla.2-4.py310",
                                    Command = { "python3", $"/gcs/{BucketName}/scripts/train_{clf.Id}.py" },
                                    Args =
                                    {
                                        $"--dataset_path=gs://{BucketName}/dataset/{clf.DatasetFolder}",
                                        $"--output_path=gs://{BucketName}/models",
                                        $"--model_name={clf.ModelName}"
                                    },
                                    Env =
                                    {
                                        new EnvVar { Name = "BUCKET_NAME", Value = BucketName },
                                        new EnvVar { Name = "CLASSIFIER_ID", Value = clf.Id }
                                    }
                                }
                            }
                        }
                    }
                };

                var parent = $"projects/{ProjectId}/locations/{Region}";
                var response = await jobClient.CreateCustomJobAsync(parent, customJob);

                return Ok(new
                {
                    message = $"Навчання '{clf.DisplayName}' запущено",
                    jobId = response.Name,
                    jobName,
                    state = response.State.ToString()
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = $"Помилка запуску: {ex.Message}" });
            }
        }

        // ── Статус завдання ───────────────────────────────────────────────────
        [HttpGet("status/{jobName}")]
        public async Task<ActionResult<object>> GetJobStatus(string jobName)
        {
            try
            {
                var credential = GetCredential()
                    .CreateScoped("https://www.googleapis.com/auth/cloud-platform");

                var clientBuilder = new JobServiceClientBuilder
                {
                    Endpoint = $"{Region}-aiplatform.googleapis.com",
                    GoogleCredential = credential
                };
                var jobClient = await clientBuilder.BuildAsync();

                // jobName приходить як base64 щоб уникнути проблем з "/" в URL
                var decodedName = System.Text.Encoding.UTF8.GetString(
                    Convert.FromBase64String(jobName));

                var job = await jobClient.GetCustomJobAsync(decodedName);

                return Ok(new
                {
                    name = job.DisplayName,
                    state = job.State.ToString(),
                    createTime = job.CreateTime?.ToDateTimeOffset().ToString("yyyy-MM-dd HH:mm:ss"),
                    endTime = job.EndTime?.ToDateTimeOffset().ToString("yyyy-MM-dd HH:mm:ss"),
                    error = job.Error?.Message
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = ex.Message });
            }
        }

        // ── Список всіх завдань ───────────────────────────────────────────────
        [HttpGet("jobs")]
        public async Task<ActionResult<object>> GetJobs()
        {
            try
            {
                var credential = GetCredential()
                    .CreateScoped("https://www.googleapis.com/auth/cloud-platform");

                var clientBuilder = new JobServiceClientBuilder
                {
                    Endpoint = $"{Region}-aiplatform.googleapis.com",
                    GoogleCredential = credential
                };
                var jobClient = await clientBuilder.BuildAsync();

                var parent = $"projects/{ProjectId}/locations/{Region}";
                var jobs = jobClient.ListCustomJobsAsync(parent);

                var result = new List<object>();
                await foreach (var job in jobs)
                {
                    result.Add(new
                    {
                        name = job.DisplayName,
                        fullName = job.Name,
                        fullNameBase64 = Convert.ToBase64String(
                            System.Text.Encoding.UTF8.GetBytes(job.Name)),
                        state = job.State.ToString(),
                        createTime = job.CreateTime?.ToDateTimeOffset().ToString("yyyy-MM-dd HH:mm:ss"),
                        endTime = job.EndTime?.ToDateTimeOffset().ToString("yyyy-MM-dd HH:mm:ss")
                    });
                }

                // Сортуємо за часом створення (нові першими)
                result = result
                    .OrderByDescending(j => ((dynamic)j).createTime)
                    .Take(20)
                    .ToList();

                return Ok(result);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = ex.Message });
            }
        }

        // ── Завантажити готову модель з GCS на сервер ─────────────────────────
        [HttpPost("download/{classifierId}")]
        public async Task<ActionResult<object>> DownloadModel(string classifierId)
        {
            try
            {
                var clf = Classifiers.FirstOrDefault(c => c.Id == classifierId);
                if (clf == null)
                    return NotFound(new { message = $"Класифікатор '{classifierId}' не знайдено" });

                var storageClient = await CreateStorageClient();

                var objectName = $"models/{clf.ModelName}_best.pth";
                var localDir = Path.Combine(_env.WebRootPath, "models");
                Directory.CreateDirectory(localDir);
                var localPath = Path.Combine(localDir, $"{clf.ModelName}_best.pth");

                using var fileStream = System.IO.File.Create(localPath);
                await storageClient.DownloadObjectAsync(BucketName, objectName, fileStream);

                return Ok(new
                {
                    message = $"Модель '{clf.DisplayName}' завантажено",
                    localPath = $"/models/{clf.ModelName}_best.pth"
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = $"Помилка завантаження: {ex.Message}" });
            }
        }

        // ── Завантажити всі готові моделі ─────────────────────────────────────
        [HttpPost("download-all")]
        public async Task<ActionResult<object>> DownloadAllModels()
        {
            var results = new List<object>();

            foreach (var clf in Classifiers)
            {
                try
                {
                    var storageClient = await CreateStorageClient();
                    var objectName = $"models/{clf.ModelName}_best.pth";
                    var localDir = Path.Combine(_env.WebRootPath, "models");
                    Directory.CreateDirectory(localDir);
                    var localPath = Path.Combine(localDir, $"{clf.ModelName}_best.pth");

                    using var fileStream = System.IO.File.Create(localPath);
                    await storageClient.DownloadObjectAsync(BucketName, objectName, fileStream);

                    results.Add(new { classifier = clf.Id, status = "ok" });
                }
                catch (Exception ex)
                {
                    results.Add(new { classifier = clf.Id, status = "error", message = ex.Message });
                }
            }

            return Ok(new { results });
        }

        // ── Допоміжні методи ──────────────────────────────────────────────────
        private async Task<StorageClient> CreateStorageClient()
        {
            var credential = GetCredential();
            return await StorageClient.CreateAsync(credential);
        }

        // ── Модель даних ──────────────────────────────────────────────────────
        private record ClassifierInfo(string Id, string DatasetFolder, string ModelName)
        {
            public string DisplayName => Id switch
            {
                "ramp"            => "Пандус",
                "lit"             => "Освітлення",
                "smoothness"      => "Нерівність",
                "width"           => "Ширина",
                "tactile"         => "Тактильна плитка",
                "surface_quality" => "Якість покриття",
                "surface_type"    => "Тип покриття",
                _ => Id
            };
        }
    }
}
