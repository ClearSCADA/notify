// Driver code for the Geo SCADA Notify demonstration
using System;
using System.Collections.Generic;
using System.Text;
using ClearSCADA.DBObjFramework;
using ClearSCADA.DriverFramework;
using Notify;
using System.Web.Script.Serialization;
using System.Security.Cryptography.X509Certificates;
using System.Diagnostics;
using System.Net;
using System.Web;

namespace DriverNotify
{

    class DriverNotify
	{
		static void Main(string[] args)
		{

			using (DriverApp App = new DriverApp())
			{
				// Init the driver with the database module
				if (App.Init(new CSharpModule(), args))
				{
					// Do custom driver init here
					App.Log("Notify driver started");

					// Start the driver MainLoop
					App.MainLoop();
				}
			}
		}
	}



	public class DrvNotifyChannel : DriverChannel<NotifyChannel>
	{
		public void LogAndEvent(string Message)
		{
			Log(Message);
			if (this.DBChannel.EnhancedEvents)
			{
				App.SendReceiveObject(this.DBChannel.Id, OPCProperty.SendRecLogChannelEventText, Message);
			}
		}

		public override void OnDefine()
		{
			// Called when enabled or config changed. Not on startup
			base.OnDefine();

			LogAndEvent("Channel Defined: " + (string)DBChannel.RedirectorHost);
		}

		public override void OnConnect()
		{
			LogAndEvent("OnConnect Channel Online.");

			base.OnConnect();
		}

		public override void OnPoll()
		{
			LogAndEvent("Channel OnPoll: Online.");

			// No need to call base class. Return exception on failure of connection
			SetStatus(ServerStatus.Online, "Running");
		}

		public override void OnDisconnect()
		{
			LogAndEvent("Channel OnDisConnect()");

			base.OnDisconnect();
		}

		public override void OnUnDefine()
		{
			LogAndEvent("Channel OnUndefine()");

			base.OnUnDefine();
		}


		public override void OnExecuteAction(ClearSCADA.DriverFramework.DriverTransaction Transaction)
		{
			LogAndEvent("Driver Action - channel.");
			switch (Transaction.ActionType)
			{
				default:
					base.OnExecuteAction(Transaction);
					break;
			}
		}
	}

	public class DrvCSScanner : DriverScanner<NotifyScanner>
	{


		public override void OnReadConfiguration()
		{
			base.OnReadConfiguration();
		}

		public override SourceStatus OnDefine()
		{
			SetScanRate(DBScanner.NormalScanRate,
							DBScanner.NormalScanOffset);

			((DrvNotifyChannel)this.Channel).LogAndEvent("Scanner defined");

			return SourceStatus.Online;
		}

		public override void OnUnDefine()
		{
			base.OnUnDefine();
		}

		private static DateTime lastScan = DateTime.Now;
		public override void OnScan()
		{
			SetStatus(SourceStatus.Online);

			// Prevent super rapid scanning
			if (lastScan.AddSeconds(10) < DateTime.Now)
			{
				lastScan = DateTime.Now;
				// We are scanning for STATUS data from the Redirector web service
				// This will tell us what events to log for success/fail of messages
				// And it can advise us of alarm acknowledgements to process
				((DrvNotifyChannel)this.Channel).LogAndEvent("Poll the Redirector");
				// ToDo
				using (WebClient client = new WebClient())
				{
					string requestaddress = WebServerAddress() + "/NotifyRequest/";
					string requestparams = "?type=" + WebUtility.UrlEncode("STATUS");
					try
					{
						string response = client.DownloadString(requestaddress + requestparams);
						((DrvNotifyChannel)this.Channel).LogAndEvent("Received data: " + response.Length.ToString());

						// ERROR text is sent back by the Redirector if it can't access the Twilio service
						// If the Redirector reports an error then raise a scanner alarm
						if (response.StartsWith("ERROR"))
						{
							((DrvNotifyChannel)this.Channel).LogAndEvent(response);

							App.SendReceiveObject(this.DBScanner.Id, OPCProperty.SendRecRaiseScannerAlarm, "Poll error from Redirector" + response);
						}
						else
						{
							// Success - clear alarm if present
							App.SendReceiveObject(this.DBScanner.Id, OPCProperty.SendRecClearScannerAlarm, "");

							// And process the received data
							// Unpack the request strings - one new line per response
							string[] responses = response.Split('\n');
							foreach( string responseLine in responses)
							{
								// If not empty
								if ((responseLine ?? "").Trim() != "")
								{
									ProcessResponse(responseLine);
								}
							}
						}
					}
					catch (Exception e)
					{
						// Why this failed (e.g. 404)
						((DrvNotifyChannel)this.Channel).LogAndEvent("Failed to poll Redirector: " + e.Message );

						// Driver should fail and raise alarm
						App.SendReceiveObject(this.DBScanner.Id, OPCProperty.SendRecRaiseScannerAlarm, e.Message);
					}
				}
			}
		}

		private void ProcessResponse( string urlencodedResponse)
		{
			// Handle each response sent back from Twilio via the Redirector
			// Responses are URLEncoded
			// Responses are designed in Twilio Flow
			Dictionary<string, string> responseParams = new Dictionary<string, string>();
			// Split response into & separated key/value pairs

			string[] paramArray = urlencodedResponse.Split('&');
			foreach( string param in paramArray)
			{
				string[] keyvalue = param.Split('=');
				if (keyvalue.Length == 2)
				{
					responseParams.Add( WebUtility.UrlDecode(keyvalue[0]), WebUtility.UrlDecode(keyvalue[1]));
				}
			}
			// Read common parameters
			string Phone = responseParams["phone"] ?? "";
			Console.WriteLine("phone:" + Phone);
			string Message = responseParams["message"] ?? "";
			Console.WriteLine("message:" + Message);
			string Cookie = responseParams["cookie"] ?? "";
			Console.WriteLine("cookie:" + Cookie);

			// Check parameter messagetype
			switch (responseParams["type"] ?? "")
			{
				case "ERRORMESSAGE":
					App.SendReceiveObject(this.DBScanner.Id, OPCProperty.SendRecLogEventText, "Notify Error: " + Message + ", Phone: " + Phone);
					break;
				// TODO
				// Further types, e.g. request to acknowledge an alarm
				default:
					break;
			}
		}

		public override void OnExecuteAction(DriverTransaction Transaction)
		{
			((DrvNotifyChannel)this.Channel).LogAndEvent("Driver Action - scanner.");
			switch (Transaction.ActionType)
			{
				// Used as the database method to send alarms and other messages to Twilio via the Redirector
				case OPCProperty.DriverActionNotifyMessage:
					{
						string Message = (string) Transaction.get_Args(0);
						string PhoneNumber = (string)Transaction.get_Args(1);
						string MessageType = (string)Transaction.get_Args(2);
						string Cookie = (string)Transaction.get_Args(3);

						((DrvNotifyChannel)this.Channel).LogAndEvent("Notify Message to: " + PhoneNumber + " using " + MessageType + " cookie " + Cookie);

						using (WebClient client = new WebClient())
						{
							((DrvNotifyChannel)this.Channel).LogAndEvent("Send Request" );
							string requestaddress = WebServerAddress() + "/NotifyRequest/";
							string requestparams = "?key=" + WebUtility.UrlEncode( this.DBScanner.APIKey) + 
													"&phone=" + WebUtility.UrlEncode( PhoneNumber) + 
													"&message=" + WebUtility.UrlEncode( Message) +
													"&type=" + WebUtility.UrlEncode( MessageType ) +
													"&cookie=" + WebUtility.UrlEncode( Cookie);
							try
							{
								string response = client.DownloadString(requestaddress + requestparams);
								((DrvNotifyChannel)this.Channel).LogAndEvent("Received data: " + response.Length.ToString());

								// If the Redirector reports an error then raise a scanner alarm
								if (response.StartsWith("ERROR"))
								{
									((DrvNotifyChannel)this.Channel).LogAndEvent(response.Substring(0, 100));

									App.SendReceiveObject(this.DBScanner.Id, OPCProperty.SendRecRaiseScannerAlarm, "Error from Redirector" + response.Substring(0,100));
								}
								else
								{
									// Success - clear alarm if present
									App.SendReceiveObject(this.DBScanner.Id, OPCProperty.SendRecClearScannerAlarm, "");
								}
							}
							catch (Exception e)
							{
								// Why this failed (e.g. 404)
								((DrvNotifyChannel)this.Channel).LogAndEvent("Failed to send to Redirector: " + e.Message.Substring(0, 100));

								// Driver should fail and raise alarm
								App.SendReceiveObject(this.DBScanner.Id, OPCProperty.SendRecRaiseScannerAlarm, e.Message.Substring(0, 100));
							}
						}
					}
					this.CompleteTransaction(Transaction, 0, "Notify Message Send Successful.");
					break;

				default:
					base.OnExecuteAction(Transaction);
					break;
			}
		}
		private string WebServerAddress()
		{
			string addr = ((DrvNotifyChannel)Channel).DBChannel.WSProtocol == 0 ? "http" : "https";
			addr += "://" + ((DrvNotifyChannel)Channel).DBChannel.RedirectorHost + ":" +
					((DrvNotifyChannel)Channel).DBChannel.RedirectorPort.ToString();
			return addr;
		}

	}

}
