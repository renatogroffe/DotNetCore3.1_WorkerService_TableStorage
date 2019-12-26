using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos.Table;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MonitoramentoSites.Entities;

namespace MonitoramentoSites
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly ServiceConfigurations _serviceConfigurations;
        private readonly JsonSerializerOptions _jsonOptions;
        private readonly CloudTable _monitoramentoTable;
        private static readonly string _versaoFramework;

        static Worker()
        {
            _versaoFramework = Assembly
               .GetEntryAssembly()?
               .GetCustomAttribute<TargetFrameworkAttribute>()?
               .FrameworkName;
        }

        public Worker(ILogger<Worker> logger,
            IConfiguration configuration)
        {
            _logger = logger;

            _serviceConfigurations = new ServiceConfigurations();
            new ConfigureFromConfigurationOptions<ServiceConfigurations>(
                configuration.GetSection("ServiceConfigurations"))
                    .Configure(_serviceConfigurations);
            _serviceConfigurations.Intervalo =
                Convert.ToInt32(configuration["Intervalo"]);

            _jsonOptions = new JsonSerializerOptions()
            {
                IgnoreNullValues = true
            };

            var storageAccount = CloudStorageAccount
                .Parse(configuration["BaseMonitoramento"]);
            _monitoramentoTable = storageAccount
                .CreateCloudTableClient().GetTableReference("Monitoramento");
            if (_monitoramentoTable.CreateIfNotExistsAsync().Result)
                _logger.LogInformation("Criando a tabela de log...");
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("Process Id: {id} - Worker executando em: {time}",
                    Process.GetCurrentProcess().Id,
                    DateTimeOffset.Now);

                foreach (string host in _serviceConfigurations.Hosts)
                {
                    _logger.LogInformation(
                        $"Verificando a disponibilidade do host {host}");

                    var resultado = new ResultadoMonitoramento();
                    resultado.Framework = _versaoFramework;
                    resultado.Horario =
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    resultado.Host = host;

                    using (var client = new HttpClient())
                    {
                        client.BaseAddress = new Uri(host);
                        client.DefaultRequestHeaders.Accept.Clear();

                        try
                        {
                            // Envio da requisicao a fim de determinar se
                            // o site esta no ar
                            HttpResponseMessage response =
                                client.GetAsync("").Result;

                            resultado.Status = (int)response.StatusCode + " " +
                                response.StatusCode;
                            if (response.StatusCode != HttpStatusCode.OK)
                                resultado.Exception = response.ReasonPhrase;
                        }
                        catch (Exception ex)
                        {
                            resultado.Status = "Exception";
                            resultado.Exception = ex.Message;
                        }
                    }

                    string jsonResultado =
                        JsonSerializer.Serialize(resultado, _jsonOptions);

                    if (resultado.Exception == null)
                        _logger.LogInformation(jsonResultado);
                    else
                        _logger.LogError(jsonResultado);


                    // Gravando o resultado utilizando Azure Table Storage
                    MonitoramentoEntity dadosMonitoramento =
                        new MonitoramentoEntity(
                            "JobMonitoramentoSites", resultado.Horario);
                    dadosMonitoramento.Local = Environment.MachineName;
                    dadosMonitoramento.DadosLog = jsonResultado;

                    var insertOperation = TableOperation.Insert(dadosMonitoramento);
                    var resultInsert = _monitoramentoTable.ExecuteAsync(insertOperation).Result;
                    _logger.LogInformation(JsonSerializer.Serialize(resultInsert));

                    Thread.Sleep(3000);
                }

                await Task.Delay(
                    _serviceConfigurations.Intervalo, stoppingToken);
            }
        }
    }
}