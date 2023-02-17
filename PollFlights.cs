using OnAir.API.Api;
using OnAir.API.Model;
using RestSharp;
using System;
using System.Linq;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;
using Azure.Storage.Blobs.Specialized;
using System.IO;
using System.Text;

namespace Handfield.OnAirMonitor
{
    public class PollFlights
    {
        // OnAir API variables
        internal static WSAPIPublicApi _onAirAPI;
        internal string _oaCompanyId = Environment.GetEnvironmentVariable("OnAirCompanyId");
        internal string _oaApiKey = Environment.GetEnvironmentVariable("OnAirApiKey");
        internal int _numFlightsToPull = Convert.ToInt32(Environment.GetEnvironmentVariable("NumFlightsToPoll"));
        internal const string _onAirApiBaseUrl = "https://server1.onair.company";

        // Storage variables
        static internal Uri _lastFlightIdProcessedUri = new Uri(Environment.GetEnvironmentVariable("LastFlightIdProcessedUri"));

        // Discord variables
        static internal Uri _discordWebhookUri = new Uri(Environment.GetEnvironmentVariable("DiscordWebhookUri"));

        [FunctionName("PollFlights")]
        public void Run([TimerTrigger("0 */1 * * * *")]TimerInfo myTimer, ILogger log)
        {
            // Set up the OnAir API service
            OnAir.API.Client.Configuration config = new OnAir.API.Client.Configuration();
            config.DefaultHeaders.Add("oa-apikey", _oaApiKey);
            _onAirAPI = new WSAPIPublicApi(config);

            // Get the Id of the last flight we've processed
            Guid lastProcessedFlightId = GetLastProcessedFlightId();
            log.LogInformation($"Retrieved last processed flight id: {lastProcessedFlightId}");

            // Request data on the last [n] flights from OnAir
            WSResultListFlight flightData = _onAirAPI.WSAPIPublicGetCompanyFlights(companyId: Guid.Parse(_oaCompanyId), limit: 10);

            int preFilterCount = flightData.Content.Count;

            // Filter out any flights that haven't been registered
            flightData.Content = flightData.Content.Where(f => f.Registered).ToList();

            // Debug
            log.LogInformation($"Filtered out unregistered flights - went from {preFilterCount} flights to {flightData.Content.Count} flights");

            // Ensure flight data is in reverse chronological order
            flightData.Content = flightData.Content.OrderByDescending(f => f.EngineOffRealTime).ToList();
            
            // Get the index of the last processed flight in the results
            int lastProcessedFlightIndex = flightData.Content.FindIndex(f => f.Id.Equals(lastProcessedFlightId));

            // Ensure we found it; if we didn't, log an error and bail out
            if (lastProcessedFlightIndex < 0)
            {
                log.LogError($"Unable to find last processed flight {lastProcessedFlightId} in results from OnAir - aborting");
                return;
            }
            
            // Debug
            log.LogInformation($"Last processed flight found in index {lastProcessedFlightIndex} of results from OnAir");

            // Record how many flights are left in the dataset before removing already-processed ones
            int preRemoveProcessedCount = flightData.Content.Count;

            // Remove any flights at or after the index returned
            flightData.Content.RemoveRange(lastProcessedFlightIndex, flightData.Content.Count - lastProcessedFlightIndex);

            // Debug
            log.LogInformation($"Filtered out already-processed flights - went from {preRemoveProcessedCount} flights to {flightData.Content.Count} flights");

            // Make sure we have some flights still to work with
            if (flightData.Content.Count > 0)
            {
                // Reverse the order of the flights so we can process in chronological order
                flightData.Content.Reverse();

                // Process each flight
                foreach (Flight flight in flightData.Content)
                {
                    // Get the XP and rep gains from the ResultComments
                    Regex reputationRegex = new Regex(@"^Reputation: (\d{1,2}\.\d{2}%)");
                    Regex distanceRegex = new Regex(@"for ([0-9,]{1,5}) NM");

                    int xpGained = flight.XPMissions;

                    Match reputationMatch = reputationRegex.Match(flight.ResultComments);
                    string repDelta = reputationMatch.Success ? reputationMatch.Captures[0].Value : "Unknown";

                    Match distanceMatch = distanceRegex.Match(flight.ResultComments);
                    string distance = distanceMatch.Success ? $"{distanceMatch.Groups[1].Value}nm" : "Unknown";

                    string discordMessage = $"Company {flight.Company.Name} ({flight.Company.AirlineCode}) completed a flight!\n\n";
                    discordMessage += $"Flew from {flight.DepartureAirport.ICAO} to {flight.ArrivalActualAirport.ICAO} ({distance}nm)\n";
                    discordMessage += $"Earned {xpGained}xp and {repDelta} reputation";

                    log.LogInformation(discordMessage);
                    SendDiscordMessage(discordMessage);
                }

                // Get the Id of the most recent flight we processed - flights are in chronological order at this point, so the last one is the most recent
                Guid mostRecentFlightId = flightData.Content.Last().Id;

                // Store that Id value in our storage account for the next execution
                bool updateSuccess = UpdateLastProcessedFlightId(mostRecentFlightId);

                if (updateSuccess)
                    log.LogInformation($"Updated last processed flight ID in storage account with new value {mostRecentFlightId}");
                else
                    log.LogError($"Error updating last processed flight ID in storage account with new value {mostRecentFlightId}");
            }
            else
            {
                log.LogInformation($"No new flights to process - exiting");
                return;
            }
        }

        private class DiscordCommand
        {
            public string Message { get; set; }
        }

        private static void SendDiscordMessage(string discordMessage)
        {
            RestClient rc = new RestClient(_discordWebhookUri);
            RestRequest request = new RestRequest(_discordWebhookUri, Method.Post);
            request.AddHeader("Content-Type", "application/json");
            var myObj = new { content = discordMessage };
            request.AddJsonBody(myObj);
            rc.ExecutePost(request);
        }

        private static Guid GetLastProcessedFlightId()
        {
            // Connect to blob storage
            BlobClient bc = new BlobClient(_lastFlightIdProcessedUri);

            // Download the Id of the last flight we processed
            BlobDownloadResult result = bc.DownloadContent();

            // Return the file data
            return Guid.Parse(result.Content.ToString());
        }

        private static bool UpdateLastProcessedFlightId(Guid latestId)
        {
            // Connect to blob storage
            BlockBlobClient bbc = new BlockBlobClient(_lastFlightIdProcessedUri);

            // Download the Id of the last flight we processed
            bbc.Upload(new MemoryStream(Encoding.UTF8.GetBytes(latestId.ToString())));

            // Return success
            return true;
        }
    }
}
