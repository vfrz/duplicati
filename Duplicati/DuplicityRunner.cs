#region Disclaimer / License
// Copyright (C) 2008, Kenneth Skovhede
// http://www.hexad.dk, opensource@hexad.dk
// 
// This library is free software; you can redistribute it and/or
// modify it under the terms of the GNU Lesser General Public
// License as published by the Free Software Foundation; either
// version 2.1 of the License, or (at your option) any later version.
// 
// This library is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
// Lesser General Public License for more details.
// 
// You should have received a copy of the GNU Lesser General Public
// License along with this library; if not, write to the Free Software
// Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301  USA
// 
#endregion
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Text;
using Duplicati.Datamodel;
using System.Drawing;

namespace Duplicati
{
    /// <summary>
    /// This class encapsulates all communication with Duplicity
    /// </summary>
    public class DuplicityRunner
    {
        private StringDictionary m_environment;

        private enum DuplicityTaskType
        {
            IncrementalBackup,
            FullBackup,
            RemoveAllButNFull,
            RemoveOlderThan,
            Restore,
            List
        }

        public DuplicityRunner(string apppath, StringDictionary environment)
        {
            if (!apppath.EndsWith(System.IO.Path.DirectorySeparatorChar.ToString()))
                apppath += System.IO.Path.DirectorySeparatorChar;
            System.Environment.SetEnvironmentVariable(ApplicationSettings.APP_PATH_ENV.Substring(1, ApplicationSettings.APP_PATH_ENV.Length - 2) , apppath);
            m_environment = new StringDictionary();
            if (environment != null)
                foreach (string k in environment.Keys)
                    m_environment[k] = environment[k];
        }

        private System.Diagnostics.ProcessStartInfo SetupEnv(Task task, DuplicityTaskType type, string extraparam)
        {
            List<string> args = new List<string>();
            System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo();

            StringDictionary env = psi.EnvironmentVariables;

            foreach (string key in m_environment.Keys)
                env[key] = m_environment[key];

            env["PATH"] = System.Environment.ExpandEnvironmentVariables(Program.ApplicationSettings.PGPPath) + System.IO.Path.PathSeparator + env["PATH"];

            args.Add("\"" + System.Environment.ExpandEnvironmentVariables(Program.ApplicationSettings.DuplicityPath) + "\"");

            switch (type)
            {
                case DuplicityTaskType.IncrementalBackup:
                    args.Add("incremental");
                    break;
                case DuplicityTaskType.FullBackup:
                    args.Add("full");
                    break;
                case DuplicityTaskType.RemoveAllButNFull:
                    args.Add("remove-all-but-n-full");
                    args.Add(extraparam);
                    break;
                case DuplicityTaskType.RemoveOlderThan:
                    args.Add("remove-older-than");
                    args.Add(extraparam);
                    break;
                case DuplicityTaskType.Restore:
                    break;
                case DuplicityTaskType.List:
                    args.Add("collection-status");
                    break;

                default:
                    throw new Exception("Bad duplicity operation given: " + type.ToString());
            }

            switch (type)
            {
                case DuplicityTaskType.FullBackup:
                case DuplicityTaskType.IncrementalBackup:
                    args.Add("\"" + System.Environment.ExpandEnvironmentVariables(task.SourcePath) + "\"");
                    args.Add("\"" + System.Environment.ExpandEnvironmentVariables(task.GetDestinationPath()) + "\"");
                    break;
                case DuplicityTaskType.RemoveAllButNFull:
                case DuplicityTaskType.RemoveOlderThan:
                    args.Add("\"" + System.Environment.ExpandEnvironmentVariables(task.GetDestinationPath()) + "\"");
                    break;
                case DuplicityTaskType.Restore:
                case DuplicityTaskType.List:
                    args.Add("\"" + System.Environment.ExpandEnvironmentVariables(task.GetDestinationPath()) + "\"");
                    args.Add("\"" + System.Environment.ExpandEnvironmentVariables(task.SourcePath) + "\"");
                    break;
                default:
                    throw new Exception("Bad duplicity operation given: " + type.ToString());
            }

            if (type == DuplicityTaskType.IncrementalBackup && !string.IsNullOrEmpty(extraparam))
            {
                args.Add("--full-if-older-than");
                args.Add(extraparam);
            }

            if (string.IsNullOrEmpty(task.Encryptionkey))
                args.Add("--no-encryption");
            else
                env["PASSPHRASE"] = task.Encryptionkey;

            if (!string.IsNullOrEmpty(task.Signaturekey))
            {
                args.Add("--sign-key");
                args.Add(task.Signaturekey);
            }

            if (type == DuplicityTaskType.RemoveAllButNFull || type == DuplicityTaskType.RemoveOlderThan)
                args.Add("--force");

            task.GetExtraSettings(args, env);


            psi.CreateNoWindow = true;
            psi.WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden;
            psi.FileName = System.Environment.ExpandEnvironmentVariables(Program.ApplicationSettings.PythonPath);
            psi.RedirectStandardError = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardInput = false;
            psi.UseShellExecute = false;
            psi.WorkingDirectory = System.IO.Path.GetDirectoryName(psi.FileName);


            psi.Arguments = string.Join(" ", args.ToArray());
            return psi;
        }

        private string PerformBackup(Task task, bool forceFull, string fullAfter)
        {
            DateTime beginTime = DateTime.Now;

            System.Diagnostics.Process p = new System.Diagnostics.Process();
            p.StartInfo = SetupEnv(task, forceFull ? DuplicityTaskType.FullBackup : DuplicityTaskType.IncrementalBackup, fullAfter);
            p.Start();

            p.WaitForExit();


            string errorstream = p.StandardError.ReadToEnd();
            string outstream = p.StandardOutput.ReadToEnd();

            string logentry = "";
            if (!string.IsNullOrEmpty(errorstream))
            {
                string tmp = errorstream.Replace("gpg: CAST5 encrypted data", "").Replace("gpg: encrypted with 1 passphrase", "").Trim();

                if (tmp.Length > 0)
                    logentry += "** Error stream: \r\n" + errorstream + "\r\n**\r\n";
            }
            logentry += outstream;

            lock (Program.MainLock)
            {
                Log l = task.DataParent.Add<Log>();
                LogBlob lb = task.DataParent.Add<LogBlob>();
                lb.StringData = logentry;

                l.LogBlob = lb;
                task.Logs.Add(l);

                //Keep some of the data in an easy to read manner
                DuplicityOutputParser.ParseData(l);
                l.SubAction = "Primary";
                l.Action = "Backup";
                l.BeginTime = beginTime;
                l.EndTime = DateTime.Now;

                task.DataParent.CommitAll();
                Program.DataConnection.CommitAll();
            }

            return logentry;
        }

        public string IncrementalBackup(Task task)
        {
            return PerformBackup(task, false, null);
        }

        public string FullBackup(Task task)
        {
            return PerformBackup(task, true, null);
        }

        public void Execute(Schedule schedule)
        {
            if (schedule.Tasks == null || schedule.Tasks.Count == 0)
                throw new Exception("No tasks were assigned to the schedule");

            Task task = schedule.Tasks[0];

            PerformBackup(task, false, schedule.FullAfter);

            if (schedule.KeepFull > 0)
            {
                DateTime beginTime = DateTime.Now;

                System.Diagnostics.Process p = new System.Diagnostics.Process();
                p.StartInfo = SetupEnv(task, DuplicityTaskType.RemoveAllButNFull, schedule.KeepFull.ToString());
                
                p.Start();
                p.WaitForExit();

                string errorstream = p.StandardError.ReadToEnd();
                string outstream = p.StandardOutput.ReadToEnd();

                string logentry = "";
                if (!string.IsNullOrEmpty(errorstream))
                    logentry += "** Error stream: \r\n" + errorstream + "\r\n**\r\n";
                logentry += outstream;

                lock (Program.MainLock)
                {
                    Log l = task.DataParent.Add<Log>();
                    LogBlob lb = task.DataParent.Add<LogBlob>();
                    lb.StringData = logentry;

                    l.LogBlob = lb;
                    task.Logs.Add(l);

                    //Keep some of the data in an easy to read manner
                    DuplicityOutputParser.ParseData(l);
                    l.SubAction = "Cleanup";
                    l.Action = "Backup";
                    l.BeginTime = beginTime;
                    l.EndTime = DateTime.Now;
                }
            }

            if (!string.IsNullOrEmpty(schedule.KeepTime))
            {
                DateTime beginTime = DateTime.Now;

                System.Diagnostics.Process p = new System.Diagnostics.Process();
                p.StartInfo = SetupEnv(task, DuplicityTaskType.RemoveOlderThan, schedule.KeepTime);

                p.Start();
                p.WaitForExit();

                string errorstream = p.StandardError.ReadToEnd();
                string outstream = p.StandardOutput.ReadToEnd();

                string logentry = "";
                if (!string.IsNullOrEmpty(errorstream))
                    logentry += "\r\n** Error stream: \r\n" + errorstream + "\r\n**\r\n";
                logentry += outstream;

                lock (Program.MainLock)
                {
                    Log l = task.DataParent.Add<Log>();
                    LogBlob lb = task.DataParent.Add<LogBlob>();
                    lb.StringData = logentry;

                    l.LogBlob = lb;
                    task.Logs.Add(l);

                    //Keep some of the data in an easy to read manner
                    DuplicityOutputParser.ParseData(l);
                    l.SubAction = "Cleanup";
                    l.Action = "Backup";
                    l.BeginTime = beginTime;
                    l.EndTime = DateTime.Now;
                }
            }

            lock (Program.MainLock)
            {
                //TODO: Fix this once commit recursive is implemented
                task.DataParent.CommitAll();
                Program.DataConnection.CommitAll();
            }
        }
    }
}
