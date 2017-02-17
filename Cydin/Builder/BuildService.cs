﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web;
using System.Xml;
using System.Xml.Serialization;
using Cydin.Models;
using Cydin.Properties;
using Mono.Addins.Setup;
using System.Net.Mail;
using System.Net;

namespace Cydin.Builder
{
	public class BuildService
	{
		static bool connected;
		static string status;
		static Thread repoUpdaterThread;
		static AutoResetEvent updateEvent = new AutoResetEvent (false);
		static object logLock = new object ();
		static object eventLock = new object ();
		static object localLock = new object ();
		static bool updateAllRequested;
		static TextWriter eventsStream;
		static ManualResetEvent eventsTreamClosed;
		
		static BuildService ()
		{
			status = "Build bot not connected";

			// Required to avoid problems when sending mails through SSL
			System.Net.ServicePointManager.ServerCertificateValidationCallback = delegate {
				return true;
			};
		}
		
		public static string AuthorisedBuildBotConnection {
			get { return Settings.Default.BuildServiceAddress; }
		}
		
		public static bool AllowChangingService {
			get { return Settings.Default.AllowChangingService; }
			set {
				if (!value)
					BuildBotConnectionRequest = null;
				Settings.Default.AllowChangingService = value;
				Settings.Default.Save ();
			}
		}
		
		public static string BuildBotConnectionRequest { get; private set; }
		
		public static string Status {
			get { return status; }
			set { status = value; }
		}
		
		public static bool IsConnected {
			get {
				return connected;
			}
		}
		
		public static bool Connect ()
		{
			string addr = HttpContext.Current.Request.UserHostAddress;
			if (addr == AuthorisedBuildBotConnection) {
				status = "Build bot identified";
				connected = true;
				return true;
			}
			else {
				BuildBotConnectionRequest = HttpContext.Current.Request.UserHostAddress;
				return false;
			}
		}
		
		public static void Disconnect ()
		{
			Settings.Default.BuildServiceAddress = null;
			Settings.Default.Save ();
			status = "Build bot disconnected";
		}
		
		public static void AuthorizeServiceConnection (string address)
		{
			Settings.Default.BuildServiceAddress = address;
			Settings.Default.AllowChangingService = false;
			Settings.Default.Save ();
		}
		
		public static void CheckClient ()
		{
			if (HttpContext.Current.Request.UserHostAddress != AuthorisedBuildBotConnection)
				throw new Exception ("Not authorised");
		}
		
		public static void BuildAll ()
		{
			NotifyEvent ("build", -1, -1);
		}
		
		public static void BuildAll (int appId)
		{
			NotifyEvent ("build", appId, -1);
		}
		
		public static void Build (int appId, int projectId)
		{
			NotifyEvent ("build", appId, projectId);
		}
		
		internal static WaitHandle ConnectEventsStream (TextWriter tw)
		{
			lock (eventLock) {
				if (eventsStream != null) {
					try {
						eventsStream.Close ();
						eventsTreamClosed.Set ();
					} catch {}
				}
				status ="Build bot connected";
				eventsStream = tw;
				return eventsTreamClosed = new ManualResetEvent (false);
			}
		}
		
		static void NotifyEvent (string eventId, int appId, int projectId, params string[] args)
		{
			lock (eventLock) {
				if (eventsStream != null) {
					try {
						eventsStream.WriteLine ("[event]");
						eventsStream.WriteLine (eventId);
						eventsStream.WriteLine (appId.ToString ());
						eventsStream.WriteLine (projectId.ToString ());
						eventsStream.WriteLine (args.Length.ToString ());
						eventsStream.Flush ();
					}
					catch {
						try {
							eventsStream.Close ();
						} catch { }
						eventsTreamClosed.Set ();
						eventsStream = null;
						status = "Build bot identified";
					}
				}
			}
		}
		
		public static void Log (Exception ex)
		{
			Log (LogSeverity.Error, ex.ToString ());
		}
		
		public static void Log (LogSeverity severity, string message)
		{
			string txt = severity + " [" + DateTime.Now.ToLongTimeString () + "] " + message + "\n";
			lock (logLock) {
				File.AppendAllText (LogFile, txt);
			}
		}
		
		public static void UpdateRepositories (bool forceUpdate)
		{
			updateAllRequested = updateAllRequested || forceUpdate;
			lock (localLock) {
				if (repoUpdaterThread == null) {
					repoUpdaterThread = new Thread (RunUpdater);
					repoUpdaterThread.IsBackground = true;
					repoUpdaterThread.Start ();
				}
			}
			updateEvent.Set ();
		}
		
		static void RunUpdater ()
		{
			while (true)
			{
				updateEvent.WaitOne ();
				try {
					bool updateAll = updateAllRequested;
					updateAllRequested = false;
					UpdateAllRepos (updateAll);
				} catch (Exception ex) {
					Log (LogSeverity.Error, "Repository update failed:");
					Log (ex);
				}
			}
		}
		
		static void UpdateAllRepos (bool updateAll)
		{
			ServiceModel sm = ServiceModel.GetCurrent ();
			var apps = sm.GetApplications ();
			sm.Dispose ();
			
			foreach (Cydin.Models.Application app in apps) {
				try {
					UpdateRepos (app.Id, updateAll);
				} catch (Exception ex) {
					Log (LogSeverity.Error, "Repository update failed:");
					Log (ex);
				}
			}
		}
		
		static void UpdateRepos (int appId, bool updateAll)
		{
			using (UserModel m = UserModel.GetAdmin (appId)) {
				string basePath = AddinsPath;
				cachedCompatibleVersions = new Dictionary<string, string[]> ();
	
				SetupService setupService = new SetupService ();
				LocalStatusMonitor monitor = new LocalStatusMonitor ();
	
				HashSet<string> fileList = new HashSet<string> ();
				FindFiles (fileList, basePath);
				HashSet<string> reposToBuild = new HashSet<string> ();
	
				HashSet<Release> releases = new HashSet<Release> ();
				var allReleases = m.GetReleases ().ToList ();

				List<AppRelease> allAppReleases = m.GetAppReleases ().ToList ();

				foreach (Release rel in allReleases) {
					if (rel.Status == ReleaseStatus.Deleted)
						continue;

					// Register the add-in for each compatible repo
					foreach (var appVersion in GetNewerCompatibleAppVersions (m, allAppReleases, rel.TargetAppVersion)) {
						foreach (string plat in rel.PlatformsList) {
							if (!IsLatestRelease (m, allAppReleases, allReleases, rel, plat, appVersion))
								continue;
							string repoPath = Path.Combine (basePath, rel.DevStatus.ToString ());
							repoPath = Path.Combine (repoPath, plat);
							repoPath = Path.Combine (repoPath, appVersion);
							string path = Path.GetFullPath (Path.Combine (repoPath, rel.AddinId + "-" + rel.Version + ".mpack"));
							fileList.Remove (path);
							if (rel.Status == ReleaseStatus.PendingPublish || updateAll) {
								if (!Directory.Exists (repoPath))
									Directory.CreateDirectory (repoPath);
								var relPath = rel.SourceTagId != null ? rel.GetFilePath (plat) : rel.GetPublishedPath (plat);

								if (!File.Exists (relPath)) {
									Log (LogSeverity.Error, "Could not publish release " + rel.Version + " of add-in " + rel.AddinId + ". File " + relPath + " not found");
									continue;
								}
								if (Path.GetFullPath (relPath) != path)
									File.Copy (relPath, path, true);
								GenerateInstallerFile (m, path, rel, plat);
								reposToBuild.Add (repoPath);
								if (!releases.Contains (rel) && rel.Status == ReleaseStatus.PendingPublish)
									releases.Add (rel);
							}
						}
					}
				}
				foreach (AppRelease arel in allAppReleases) {
					foreach (object status in Enum.GetValues (typeof(DevStatus))) {
						foreach (string plat in m.CurrentApplication.PlatformsList) {
							string repoPath = Path.Combine (basePath, status.ToString ());
							repoPath = Path.Combine (repoPath, plat);
							repoPath = Path.Combine (repoPath, arel.AppVersion);
							if (!Directory.Exists (repoPath)) {
								Directory.CreateDirectory (repoPath);
								reposToBuild.Add (repoPath);
							}
							else if (!File.Exists (Path.Combine (repoPath, "main.mrep")) || updateAll)
								reposToBuild.Add (repoPath);
						}
					}
				}
	
				// Remove old add-ins
	
				foreach (string f in fileList) {
					try {
						reposToBuild.Add (Path.GetFullPath (Path.GetDirectoryName (f)));
						File.Delete (f);
						string f2 = Path.ChangeExtension (f, m.CurrentApplication.AddinPackageExtension);
						if (File.Exists (f2))
							File.Delete (f2);
					}
					catch (Exception ex) {
						Log (ex);
					}
				}
	
				// Update the repos

				var sortedRepos = reposToBuild.ToList ();
				sortedRepos.Sort ((p1,p2) => Mono.Addins.Addin.CompareVersions (Path.GetFileName (Path.GetDirectoryName (p2)), Path.GetFileName (Path.GetDirectoryName (p1))));
	
				foreach (string r in sortedRepos) {
					string mainFile = Path.Combine (r, "main.mrep");
					if (File.Exists (mainFile))
						File.Delete (mainFile);
					setupService.BuildRepository (monitor, r);
					string ds = r.Substring (basePath.Length + 1);
					int i = ds.IndexOf (Path.DirectorySeparatorChar);
					ds = ds.Substring (0, i);
					string title = m.CurrentApplication.Name + " Add-in Repository";
					if (ds != DevStatus.Stable.ToString())
						title += " (" + ds + " channel)";
					AppendName (mainFile, title);
					AppendName (Path.Combine (r, "root.mrep"), title);
				}
	
				foreach (Release rel in releases)
					m.SetPublished (rel);
			}
		}

		static bool IsLatestRelease (UserModel m, List<AppRelease> allAppReleases, List<Release> releases, Release release, string platform, string targetAppVersion)
		{
			var compatReleases = GetCompatibleAppVersions (m, allAppReleases, targetAppVersion);
			return !releases.Any (r => r.ProjectId == release.ProjectId && 
				r.AddinId == release.AddinId && 
				(r.Status == ReleaseStatus.Published || r.Status == ReleaseStatus.PendingPublish) &&
				r.PlatformsList.Contains (platform) &&
				compatReleases.Contains (r.TargetAppVersion) &&
				(Mono.Addins.Addin.CompareVersions (r.Version, release.Version) < 0 || (r.Version == release.Version && r.LastChangeTime > release.LastChangeTime))
			);
		}
		
		static void AppendName (string file, string name)
		{
			if (!File.Exists (file))
				return;
			XmlDocument repDoc = new XmlDocument ();
			repDoc.Load (file);
			if (repDoc.DocumentElement ["Name"] != null)
				return;
			XmlElement nameElem = repDoc.CreateElement ("Name");
			repDoc.DocumentElement.AppendChild (nameElem);
			nameElem.InnerText = name;
			repDoc.Save (file);
		}

		static Dictionary<string,string[]> cachedCompatibleVersions = new Dictionary<string, string[]> ();

		static List<string> GetNewerCompatibleAppVersions (UserModel m, List<AppRelease> allAppReleases, string appVersion)
		{
			List<string> res = new List<string> ();
			foreach (var ar in allAppReleases) {
				if (GetCompatibleAppVersions (m, allAppReleases, ar.AppVersion).Contains (appVersion))
					res.Add (ar.AppVersion);
			}
			return res;
		}

		static string[] GetCompatibleAppVersions (UserModel m, List<AppRelease> allAppReleases, string appVersion)
		{
			string[] res;
			if (!cachedCompatibleVersions.TryGetValue (appVersion, out res)) {
				var appRel = m.GetAppReleaseByVersion (appVersion);

				List<string> versions = new List<string> ();
				versions.Add (appVersion);

				if (appRel.CompatibleAppReleaseId.HasValue) {
					var compatRel = m.GetAppRelease (appRel.CompatibleAppReleaseId.Value);
					if (compatRel != null) {
						foreach (var app in allAppReleases) {
							if (Mono.Addins.Addin.CompareVersions (app.AppVersion, appRel.AppVersion) > 0 && Mono.Addins.Addin.CompareVersions (compatRel.AppVersion, app.AppVersion) >= 0)
								versions.Add (app.AppVersion);
						}
					}
				}

				res = cachedCompatibleVersions [appVersion] = versions.ToArray ();
			}
			return res;
		}

		static void FindFiles (HashSet<string> fileList, string path)
		{
			if (!Directory.Exists (path))
				return;
			foreach (string f in Directory.GetFiles (path, "*.mpack"))
				fileList.Add (Path.GetFullPath (f));
			foreach (string dir in Directory.GetDirectories (path))
				FindFiles (fileList, dir);
		}
		
		internal static void GenerateInstallerFile (UserModel m, string packagesPath, Release rel, params string[] platforms)
		{
			string file = Path.ChangeExtension (packagesPath, m.CurrentApplication.AddinPackageExtension);
			using (StreamWriter sw = new StreamWriter (file)) {
				GenerateInstallerXml (sw, m, rel, platforms);
			}
		}
		
		internal static void GenerateInstallerXml (TextWriter sw, UserModel m, Release rel, params string[] platforms)
		{
			XmlTextWriter tw = new XmlTextWriter (sw);
			tw.Formatting = Formatting.Indented;
			tw.WriteStartElement ("Package");
			tw.WriteStartElement ("Repositories");
			foreach (string plat in platforms) {
				tw.WriteStartElement ("Repository");
				tw.WriteAttributeString ("platform", plat);
				tw.WriteAttributeString ("appVersion", rel.TargetAppVersion);
				string url = "http://" + Settings.Default.WebSiteHost + "/" + rel.DevStatus.ToString() + "/" + plat + "/" + rel.TargetAppVersion;
				tw.WriteString (url);
				tw.WriteEndElement (); // Repository
			}
			tw.WriteEndElement (); // Repositories
			tw.WriteStartElement ("Addins");
			tw.WriteStartElement ("Addin");
			tw.WriteElementString ("Id", rel.AddinId);
			tw.WriteElementString ("Version", rel.Version);
			tw.WriteElementString ("Name", rel.AddinName);
			tw.WriteElementString ("Description", rel.AddinDescription);
			tw.WriteEndElement (); // Addin
			tw.WriteEndElement (); // Addins
			tw.WriteEndElement (); // Package
		}
		
		public static Release PublishRelease (UserModel m, SourceTag source, bool activate)
		{
			Release rel = m.GetPublishedRelease (source);
			if (rel != null)
				m.DeleteRelease (rel);

			Project p = m.GetProject (source.ProjectId);
			rel = new Release ();
			rel.ProjectId = source.ProjectId;
			rel.Status = p.HasFlag (ProjectFlag.AllowDirectPublish) || activate ? ReleaseStatus.PendingPublish : ReleaseStatus.PendingReview;
			rel.DevStatus = source.DevStatus;
			rel.LastChangeTime = DateTime.Now;
			rel.Platforms = source.Platforms;
			rel.TargetAppVersion = source.TargetAppVersion;
			rel.Version = source.AddinVersion;
			rel.SourceTagId = source.Id;
			
			string mpack = rel.GetFilePath (rel.PlatformsList [0]);
			AddinInfo ainfo = UserModel.ReadAddinInfo (mpack);
			rel.AddinId = Mono.Addins.Addin.GetIdName (ainfo.Id);
			rel.AddinName = ainfo.Name;
			rel.AddinDescription = ainfo.Description;
			
			m.CreateRelease (rel);

			if (rel.Status == ReleaseStatus.PendingPublish)
				BuildService.UpdateRepositories (false);

			return rel;
		}

		internal static void RunCommand (string command, string args, StringBuilder output, StringBuilder error, int timeout)
		{
			Process p = new Process ();
			ProcessStartInfo pinfo = p.StartInfo;
			pinfo.FileName = command;
			pinfo.Arguments = args;

			pinfo.UseShellExecute = false;
			pinfo.RedirectStandardOutput = true;
			pinfo.RedirectStandardError = true;
			pinfo.CreateNoWindow = true;

			p.OutputDataReceived += delegate (object sender, DataReceivedEventArgs e)
			{
				lock (output)
					output.Append (e.Data + "\n");
			};
			p.ErrorDataReceived += delegate (object sender, DataReceivedEventArgs e)
			{
				lock (error)
					error.Append (e.Data + "\n");
			};
			p.Start ();
			p.BeginOutputReadLine ();
			p.BeginErrorReadLine ();

			if (!p.WaitForExit (timeout)) {
				// Wait for 5 minutes
				output.AppendLine ("\nAborted.");
				try {
					p.Kill ();
				}
				catch { }

				throw new Exception ("Source fetch took more than 5 minutes. Aborted.");
			}
			if (p.ExitCode != 0) {
				throw new Exception ("Command failed.");
			}
		}
		
		public static void SendMail (IEnumerable<string> addresses, string subject, string body)
		{
			if (!addresses.Any ())
				return;
			
			string host = "cydin.com";
			Uri u;
			if (Uri.TryCreate ("http://" + Settings.Default.WebSiteHost, UriKind.Absolute, out u))
				host = u.Host;
				
			SmtpClient c = new SmtpClient (Settings.Default.SmtpHost, Settings.Default.SmtpPort);
			MailAddress fad = new MailAddress ("no-reply@" + host, "Community Add-in Repository", System.Text.Encoding.UTF8);
			c.EnableSsl = Settings.Default.SmtpUseSSL;
			c.Credentials = new NetworkCredential (Settings.Default.SmtpUser, Settings.Default.SmtpPassword);
			
		    MarkdownSharp.Markdown md = new MarkdownSharp.Markdown ();
		    md.AutoHyperlink = true;
		    md.AutoNewLines = true;
			body = md.Transform (body);
					
			foreach (string to in addresses) {
				MailAddress tad = new MailAddress (to);
				MailMessage msg = new MailMessage (fad, tad);
				msg.Body = body;
				msg.BodyEncoding = System.Text.Encoding.UTF8;
				msg.Subject = subject;
				msg.SubjectEncoding = System.Text.Encoding.UTF8;
				msg.IsBodyHtml = true;
				c.Send (msg);
				//Console.WriteLine ("pp SENDING MAIL TO: " + to + " - " + subject);
			}
		}
		
		public static string DataPath {
			get {
				string basePath = Settings.Default.DataPath;
				if (!Path.IsPathRooted (basePath))
					return Path.Combine (Settings.BasePath, basePath);
				else
					return basePath;
			}
		}
		
		public static string AddinsPath {
			get {
				return Path.Combine (DataPath, "Addins");
			}
		}
		
		public static string PackagesPath {
			get {
				return Path.Combine (DataPath, "Packages");
			}
		}
		
		public static string LogFile {
			get {
				return Path.Combine (DataPath, "cydin.log");
			}
		}
		
		public static string ConfigFile {
			get {
				return Path.Combine (DataPath, "cydin.config");
			}
		}
	}
	
	public enum LogSeverity
	{
		Info,
		Warning,
		Error
	}
	
	public class ServerConfiguration
	{
		public ServerConfiguration ()
		{
			BuildServiceAddress = "127.0.0.1";
		}
		
		public string BuildServiceAddress { get; set; }
		
		public bool AllowChangingService { get; set; }
	}
}