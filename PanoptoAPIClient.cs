using System.Net;
using System.Net.Http.Headers;
using System.ServiceModel;
using System.ServiceModel.Channels;
using System.Text.Json;
using PanoProxy.Auth;
using PanoProxy.RemoteRecorderManagement;
using PanoProxy.SessionManagement;

namespace PanoProxy
{
    // --- Helper Classes for JSON Deserialization ---
    public class PanoptoSessionMetadata { public Guid SessionPublicId { get; init; } }

    public class PanoptoApiClient : IDisposable
    {
        private readonly CookieContainer _cookieContainer;
        private readonly HttpClient _httpClient;
        private readonly string _username;
        private readonly string _password;

        private readonly AuthClient _authClient;
        private readonly RemoteRecorderManagementClient _recorderManagementClient;
        private readonly SessionManagementClient _sessionsClient;

        private Auth.AuthenticationInfo? _soapAuthInfo;
        private bool _isDisposed;
        private bool _isLoggedIn;
        private readonly SemaphoreSlim _loginLock = new (1, 1);

        public PanoptoApiClient(string panoptoHostname, string username, string password)
        {
            if (string.IsNullOrWhiteSpace(panoptoHostname))
                throw new ArgumentNullException(nameof(panoptoHostname));
            if (string.IsNullOrWhiteSpace(username))
                throw new ArgumentNullException(nameof(username));
            if (string.IsNullOrWhiteSpace(password))
                throw new ArgumentNullException(nameof(password));

            var panoptoBaseUrl = panoptoHostname.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                              panoptoHostname.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? panoptoHostname
                : $"https://{panoptoHostname}";

            _username = username;
            _password = password;
            _cookieContainer = new CookieContainer();

            var soapBinding = new BasicHttpBinding(BasicHttpSecurityMode.Transport)
            {
                MaxReceivedMessageSize = 20000000,
                SendTimeout = TimeSpan.FromMinutes(5),
                ReceiveTimeout = TimeSpan.FromMinutes(5),
                AllowCookies = true
            };

            try
            {
                _authClient = new AuthClient(soapBinding, new EndpointAddress($"{panoptoBaseUrl}/Panopto/PublicAPI/4.6/Auth.svc"));
                _recorderManagementClient = new RemoteRecorderManagementClient(soapBinding, new EndpointAddress($"{panoptoBaseUrl}/Panopto/PublicAPI/4.6/RemoteRecorderManagement.svc"));
                _sessionsClient = new SessionManagementClient(soapBinding, new EndpointAddress($"{panoptoBaseUrl}/Panopto/PublicAPI/4.6/SessionManagement.svc"));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"CRITICAL Error initializing SOAP clients: {ex}");
                throw new InvalidOperationException($"Failed to initialize SOAP clients. Check Panopto URL and service availability. Details: {ex.Message}", ex);
            }

            var handler = new HttpClientHandler
            {
                CookieContainer = _cookieContainer,
                UseCookies = true,
                AllowAutoRedirect = true,
            };
            _httpClient = new HttpClient(handler) { BaseAddress = new Uri(panoptoBaseUrl) };
            _httpClient.DefaultRequestHeaders.Accept.Clear();
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }

        private async Task<bool> EnsureLoggedInAsync()
        {
            if (_isLoggedIn)
                return true;

            await _loginLock.WaitAsync();
            try
            {
                if (_isLoggedIn)
                    return true;

                return await LoginInternalAsync();
            }
            finally
            {
                _loginLock.Release();
            }
        }

        private async Task<bool> LoginInternalAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    var serviceUri = _authClient.Endpoint.Address.Uri;
                    using (var scope = new OperationContextScope(_authClient.InnerChannel))
                    {
                        _authClient.LogOnWithPassword(_username, _password);
                        MessageProperties properties = OperationContext.Current.IncomingMessageProperties;
                        HttpResponseMessageProperty? httpResponse = properties[HttpResponseMessageProperty.Name] as HttpResponseMessageProperty;

                        if (httpResponse != null)
                        {
                            string? setCookieHeader = httpResponse.Headers[HttpResponseHeader.SetCookie];
                            if (!string.IsNullOrEmpty(setCookieHeader))
                            {
                                _cookieContainer.SetCookies(serviceUri, setCookieHeader);
                                Console.WriteLine($"Login: Processed Set-Cookie for {serviceUri}. Cookies in shared container: {_cookieContainer.GetCookieHeader(serviceUri)}");
                            }
                            else { Console.WriteLine("Login: No Set-Cookie header found in the response."); }
                        }
                        else { Console.WriteLine("Login: HttpResponseMessageProperty not found. Cannot retrieve cookies from WCF response."); }
                    }
                    _soapAuthInfo = new Auth.AuthenticationInfo() { UserKey = _username, Password = _password };
                    _isLoggedIn = true;
                    Console.WriteLine("SOAP Login successful. _soapAuthInfo populated.");
                    return true;
                }
                catch (FaultException faultEx) { Console.WriteLine($"SOAP Fault during Login: {faultEx}"); _isLoggedIn = false; return false; }
                catch (Exception ex) { Console.WriteLine($"Error during SOAP Login or cookie processing: {ex}"); _isLoggedIn = false; return false; }
            });
        }

        public async Task<string> GetRemoteRecorderStateAsync(Guid remoteRecorderId)
        {
            if (!await EnsureLoggedInAsync())
            {
                Console.WriteLine("GetRemoteRecorderStateAsync Error: Login failed.");
                return "Error: Not logged in";
            }

            if (_soapAuthInfo == null)
            {
                Console.WriteLine("GetRemoteRecorderStateAsync Error: Client or AuthInfo not ready.");
                return "Error: Client not ready";
            }

            return await Task.Run(() => {
                try
                {
                    var authInfoForCall = new RemoteRecorderManagement.AuthenticationInfo { UserKey = _soapAuthInfo.UserKey, Password = _soapAuthInfo.Password };
                    var recorderList = _recorderManagementClient.GetRemoteRecordersById(authInfoForCall, new Guid[] { remoteRecorderId });
                    if (recorderList != null && recorderList.Length > 0 && recorderList[0] != null) return recorderList[0].State.ToString();
                    return "Unknown";
                }
                catch (Exception ex) { Console.WriteLine($"Error GetRemoteRecorderState: {ex}"); return "Error"; }
            });
        }

        public async Task<List<Session>> GetSessionsListAsync(Guid remoteRecorderId)
        {
            if (!await EnsureLoggedInAsync())
            {
                Console.WriteLine("GetSessionsListAsync Error: Login failed.");
                return new List<Session>();
            }

            if (_soapAuthInfo == null)
            {
                Console.WriteLine("GetSessionsListAsync Error: Client or AuthInfo not ready.");
                return new List<Session>();
            }

            return await Task.Run(async () => {
                try
                {
                    var authInfoForCall = new RemoteRecorderManagement.AuthenticationInfo { UserKey = _soapAuthInfo.UserKey, Password = _soapAuthInfo.Password };

                    var rrResponse =  _recorderManagementClient.GetRemoteRecordersById(authInfoForCall, new Guid[] { remoteRecorderId });
                    if (rrResponse.First().ScheduledRecordings.Length > 0 )
                    {
                        var sessions = await GetSessionDetailsAsync(rrResponse.First().ScheduledRecordings);
                        return sessions ?? new List<Session>();
                    }

                    return new List<Session>();
                }
                catch (FaultException faultEx)
                {
                    Console.WriteLine($"SOAP Fault in GetSessionsList: {faultEx}");
                    return new List<Session>();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error GetSessionsList: {ex}");
                    return new List<Session>();
                }
            });
        }

        public async Task<List<Session>?> GetSessionDetailsAsync(Guid[] sessionIds)
        {
            if (!await EnsureLoggedInAsync())
            {
                Console.WriteLine("GetSessionDetailsAsync Error: Login failed.");
                return null;
            }

            if (_soapAuthInfo == null)
            {
                Console.WriteLine("GetSessionDetailsAsync Error: Client or AuthInfo not ready.");
                return null;
            }

            return await Task.Run(() => {
                try
                {
                    var authInfoForCall = new SessionManagement.AuthenticationInfo { UserKey = _soapAuthInfo.UserKey, Password = _soapAuthInfo.Password };
                    var sessions = _sessionsClient.GetSessionsById(authInfoForCall,   sessionIds);
                    if (sessions != null && sessions.Length > 0) return sessions.ToList();
                    return null;
                }
                catch (Exception ex) { Console.WriteLine($"Error GetSessionDetails: {ex}"); return null; }
            });
        }

        public async Task<bool> UpdateSessionTimeAsync(Guid sessionId, DateTime newStartTime, DateTime newEndTime)
        {
            if (!await EnsureLoggedInAsync())
            {
                Console.WriteLine("UpdateSessionTimeAsync Error: Login failed.");
                return false;
            }

            // This method uses _recorderManagementClient.UpdateRecordingTime.
            // It's more general than just updating end time.
            if (_soapAuthInfo == null)
            {
                Console.WriteLine("UpdateSessionTimeAsync Error: RRClient or AuthInfo not ready.");
                return false;
            }

            return await Task.Run(() => {
                try
                {
                    var authInfoForCall = new RemoteRecorderManagement.AuthenticationInfo { UserKey = _soapAuthInfo.UserKey, Password = _soapAuthInfo.Password };
                    _recorderManagementClient.UpdateRecordingTime(authInfoForCall, sessionId, newStartTime.ToUniversalTime(), newEndTime.ToUniversalTime());
                    Console.WriteLine($"UpdateSessionTimeAsync: Called UpdateRecordingTime for Session {sessionId} to Start: {newStartTime.ToUniversalTime()}, End: {newEndTime.ToUniversalTime()}");
                    return true;
                }
                catch (Exception ex) { Console.WriteLine($"Error UpdateSessionTime (via UpdateRecordingTime): {ex}"); return false; }
            });
        }

        public async Task<Guid?> CreateRecordingAsync(Guid remoteRecorderId, string sessionName, DateTime startTime, TimeSpan duration, Guid folderId)
        {
            if (!await EnsureLoggedInAsync())
            {
                Console.WriteLine("UpdateSessionTimeAsync Error: Login failed.");
                return null;
            }

            if (_soapAuthInfo == null)
            {
                Console.WriteLine("CreateRecordingAsync Error: Client(s) or AuthInfo not ready.");
                return null;
            }

            return await Task.Run(() =>
            {
                try
                {
                    var rrAuth = new RemoteRecorderManagement.AuthenticationInfo { UserKey = _soapAuthInfo.UserKey, Password = _soapAuthInfo.Password };

                    // Step 2: Schedule it on the remote recorders
                    DateTime endTime = startTime.Add(duration);

                    // The ScheduleRecording method on IRemoteRecorderManagement is typically used.
                    // It might return a boolean or void.
                    var response =  _recorderManagementClient.ScheduleRecording(rrAuth, sessionName, folderId, false, startTime, endTime, new RecorderSettings[] { new RecorderSettings { RecorderId = remoteRecorderId } });

                    Console.WriteLine($"CreateRecordingAsync: Scheduled recording for session {response.SessionIDs.FirstOrDefault()} on RR {remoteRecorderId} from {startTime} to {endTime}");
                    return response.SessionIDs.FirstOrDefault();
                }
                catch (FaultException faultEx)
                {
                    Console.WriteLine($"SOAP Fault in CreateRecordingAsync: {faultEx}");
                    return null;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error in CreateRecordingAsync: {ex}");
                    return (Guid?)null;
                }
            });
        }

        // --- REST API Methods ---
        public async Task<Guid?> GetInternalSessionIdAsync(Guid deliveryId)
        {
            if (!await EnsureLoggedInAsync())
            {
                Console.WriteLine("GetInternalSessionIdAsync Error: Login failed.");
                return null;
            }

            try
            {
                var requestUri = $"/Panopto/PublicAPI/4.1/SessionMetadata?deliveryid={deliveryId}";
                var response = await _httpClient.GetAsync(requestUri);
                response.EnsureSuccessStatusCode();
                var responseBody = await response.Content.ReadAsStringAsync();
                var metadata = JsonSerializer.Deserialize<PanoptoSessionMetadata>(responseBody, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                return metadata?.SessionPublicId;
            }
            catch (HttpRequestException httpEx) { Console.WriteLine($"HTTP Error GetInternalSessionId:  {httpEx.Message}"); return null; }
            catch (Exception ex) { Console.WriteLine($"Error GetInternalSessionId: {ex}"); return null; }
        }

        public async Task<Guid?> PauseSessionAsync(Guid internalSessionId)
        {
            if (!await EnsureLoggedInAsync())
            {
                Console.WriteLine("PauseSessionAsync Error: Login failed.");
                return null;
            }

            try
            {
                var requestUri = $"/Panopto/PublicAPI/4.1/Pause?sessionId={internalSessionId}";
                var response = await _httpClient.PostAsync(requestUri, null);
                response.EnsureSuccessStatusCode();
                var responseBody = await response.Content.ReadAsStringAsync();
                if (Guid.TryParse(responseBody.Trim('"'), out Guid pauseId)) return pauseId;
                Console.WriteLine($"PauseSessionAsync: Could not parse Pause ID from response: {responseBody}");
                return null;
            }
            catch (HttpRequestException httpEx) { Console.WriteLine($"HTTP Error PauseSession: {httpEx.Message}"); return null; }
            catch (Exception ex) { Console.WriteLine($"Error PauseSession: {ex}"); return null; }
        }

        public async Task<bool> UpdatePauseDurationAsync(Guid internalSessionId, Guid pauseId, int durationSeconds)
        {
            if (!await EnsureLoggedInAsync())
            {
                Console.WriteLine("UpdatePauseDurationAsync Error: Login failed.");
                return false;
            }

            try
            {
                var requestUri = $"/Panopto/PublicAPI/4.1/PauseDuration?sessionId={internalSessionId}&pauseId={pauseId}&durationSeconds={durationSeconds}";
                var response = await _httpClient.PostAsync(requestUri, null);
                response.EnsureSuccessStatusCode();
                Console.WriteLine($"UpdatePauseDurationAsync for session {internalSessionId}, pause {pauseId} to {durationSeconds}s successful.");
                return true;
            }
            catch (HttpRequestException httpEx) { Console.WriteLine($"HTTP Error UpdatePauseDuration: {httpEx.Message}"); return false; }
            catch (Exception ex) { Console.WriteLine($"Error UpdatePauseDuration: {ex}"); return false; }
        }

        public void Dispose()
        {
            Dispose(true); GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_isDisposed) return;
            if (disposing)
            {
                _httpClient?.Dispose();
                _loginLock?.Dispose();
                Action<object> closeClient = (clientObj) => {
                    if (clientObj is ICommunicationObject commObj)
                    {
                        try
                        {
                            if (commObj.State != CommunicationState.Faulted && commObj.State != CommunicationState.Closed) commObj.Close();
                            else if (commObj.State == CommunicationState.Faulted) commObj.Abort();
                        }
                        catch { commObj.Abort(); }
                    }
                };
                closeClient(_authClient);
                closeClient(_recorderManagementClient);
                closeClient(_sessionsClient);
            }
            _isDisposed = true;
        }
    }
}
