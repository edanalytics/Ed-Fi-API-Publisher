namespace EdFi.Tools.ApiPublisher.Connections.Api.Configuration
{
    public class ConnectionConfiguration
    {
        public Connections Connections { get; set; }
    }

    public class Connections
    {
        public ApiConnectionDetails Source { get; set; }
        public ApiConnectionDetails Target { get; set; }
    }
}