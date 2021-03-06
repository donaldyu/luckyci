﻿using System;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml;
using common.BL;
using common.DTO;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.ServiceProcess;
using System.Threading;
using common.TOOL;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace LuckyCIService
{
    public partial class Service1 : ServiceBase
    {
        SvnController _svnController = new SvnController();
        ConfigController _configController = new ConfigController();
        ProjectController _projectController = new ProjectController();
        MailController _mailController = new MailController();                 
        static string _latestRevision;//版本号   

        public Service1()
        {
            InitializeComponent();
        }

        protected override void OnStart(string[] args)
        {
            Thread thread = new Thread(fun);
            thread.Start();
        }

        private void fun()
        {
            string config = System.AppDomain.CurrentDomain.BaseDirectory;
            int p = config.LastIndexOf("\\");
            string parent = config.Substring(0, p - 25);
            string xmlPath = parent + "\\common\\res\\CIconfig.xml";
            string buildXmlPath = parent + "\\common\\res\\BuildResultLogs.xml";
            string lastXmlPath = parent + "\\common\\res\\LastestInfo.xml";
            string mailPath = parent + "\\common\\SendMail.html";

            List<ProjectInfo> projectInfos = _projectController.ProjectQuery("/config/Projects", true,
                xmlPath);
            List<ProjectInfo> lastResults = _projectController.ReadLog("/config/Projects", buildXmlPath);
            foreach (var projectInfo in projectInfos)
            {
                if (projectInfo.Statusproperty == "true")
                {
                    //保存修改前的版本以便回滚
                    ProjectInfo lastestInfo = _projectController.ProjectQuery("config/lastest", false,
                        lastXmlPath).First();
                    //将当前运行项目及运行状态存储
                    Dictionary<string, string> startValue = new Dictionary<string, string>();
                    startValue.Add("projectName", projectInfo.Nameproperty);
                    startValue.Add("result", "running");
                    _projectController.ModifyProject(startValue, null, lastXmlPath, "lastest");
                    //更新项目,若版本号未变则无需编译否则编译
                    Boolean updateResult = UpdateProject(projectInfo,xmlPath,buildXmlPath);
                    //如果无需编译,则结果会滚到改变前的版本
                    if (updateResult)
                    {
                        Dictionary<string, string> revertLastInfo = new Dictionary<string, string>();
                        revertLastInfo.Add("projectName", lastestInfo.Nameproperty);
                        revertLastInfo.Add("result", lastestInfo.Result);
                        _projectController.ModifyProject(revertLastInfo, null,
                            lastXmlPath, "lastest");
                    }
                    else
                    {
                        //编译项目                                                                       
                        ProjectInfo buildFinishedInfo = BuildProject(projectInfo,xmlPath);
                        //存储编译信息
                        SaveInfo(buildFinishedInfo,buildXmlPath,lastXmlPath);
                        //先从本地svn完善项目信息后自动发送邮件
                        ProjectInfo projectInfoGetLocal = _svnController.GetLocalInfo(buildFinishedInfo);
                        SendSlackMail(projectInfoGetLocal, projectInfo.IfSlack, projectInfo.IfMail,mailPath);
                    }
                }
            }
            XmlNodeList xmlNodeLost = _configController.FindConfigInfo("/config/preferences/UpdateInterval", xmlPath);
            int updateInterval = int.Parse(xmlNodeLost[0].InnerText);
            Thread.Sleep(updateInterval*60000);
            Thread t = new Thread(fun);
            t.Start();
        }

        /// <summary>
        /// 执行编译项目的操作
        /// </summary>
        /// <param name="projectInfo">当前编译的项目</param>
        /// <returns></returns>
        private ProjectInfo BuildProject(ProjectInfo projectInfo,string xmlPath)
        {
            string buildResult; //存储编译是否成功
            string error;          //存储编译的日志文件
            string log;              //存储编译的所有信息
            string time;
            ConfigInfo configInfo = _configController.ConfigQuery("config/preferences",
                xmlPath);
            projectInfo.BuildTime = DateTime.Now.ToString();
            log = _projectController.Build(projectInfo.Buildcommand, projectInfo.Workdirectory,
                out buildResult, out error,out time);
            //projectInfo.Duration = Regex.Match(log, "(?<=Total time:).*?(?=secs)").Value == ""
            //    ? ""
            //    : (Regex.Match(log, "(?<=Total time:).*?(?=secs)").Value + " secs");
            projectInfo.Duration = time;
            projectInfo.Revision = _latestRevision;
            projectInfo.Log = (configInfo.StandarOutput == "true")
                ? ("\n" + buildResult + "\n" + log + "\n" + error)
                : ("\n" + buildResult + "\n" + projectInfo.Duration + "\n" + error);
            projectInfo.Result = buildResult;
            return projectInfo;
        }

        /// <summary>
        /// 存储编译的信息
        /// </summary>
        /// <param name="projectInfo">当前编译的项目</param>
        private void SaveInfo(ProjectInfo projectInfo,string buildXmlPath,string lastXmlpath)
        {
            Dictionary<string, string> lastProject = new Dictionary<string, string>();
            Dictionary<string, string> property = new Dictionary<string, string>();
            property.Add("Name", projectInfo.Nameproperty);
            //累加Log,存储在新的Log节点并执行添加属性的操作
            _projectController.SaveLog(projectInfo, property,
                buildXmlPath, "Projects");
            //修改存储最近完成项目信息的xml文件
            lastProject.Add("projectName", projectInfo.Nameproperty);
            lastProject.Add("buildTime", projectInfo.BuildTime);
            lastProject.Add("duration", projectInfo.Duration);
            lastProject.Add("result", projectInfo.Result);
            _projectController.ModifyProject(lastProject, null,
                lastXmlpath, "lastest");
        }

        /// <summary>
        /// 执行更新的操作
        /// </summary>
        /// <param name="projectInfo">所要更新的项目的相关信息</param>
        /// <param name="startValue">当前运行项目保存的信息</param>
        /// <returns>是否要更新</returns>
        private Boolean UpdateProject(ProjectInfo projectInfo,string xmlPath,string buildXmlPath)
        {
            string updateLog = "";     //执行更新操作获取的日志信息
            string updateResult = ""; //执行更新操作的结果
            updateLog = _svnController.Update(projectInfo.Workdirectory, out updateResult,xmlPath);
            //判断版本号
            _latestRevision = Regex.Match(updateLog, @"revision\s[0-9]+").Value.Replace("revision", "");
            List<ProjectInfo> infos = new List<ProjectInfo>();
            infos = _projectController.ReadLog("/config/Projects",
                buildXmlPath);
            string[] updateLogSplit = updateLog.Split('\n');
            foreach (var info in infos)
            {
                if (info.Nameproperty.Equals(projectInfo.Nameproperty))
                {
                    if (info.Revision.Equals(_latestRevision)||updateLogSplit.Length<=3)
                    {
                        return true;
                    }
                    break;
                }
            }
            return false;
        }

        /// <summary>
        /// 执行发送邮件的操作
        /// </summary>
        /// <param name="projectInfo">projectInfoGetLocal的信息</param>
        /// <param name="ifSlack">是否向slack发信息</param>
        /// <param name="ifMail">是否发送邮件</param>
        private void SendSlackMail(ProjectInfo projectInfo, string ifSlack, string ifMail,string mailPath)
        {
            if (projectInfo.IfMail == "true")
            {
                MailInfo mailInfo = _mailController.EditBody(projectInfo,
                    mailPath);
                _mailController.SendMail(mailInfo);
            }
            try
            {
                if (ifSlack == "true")
                {
                    WebClient webClient = new WebClient();
                    string slackBody = projectInfo.SlackContent;
                    slackBody = slackBody.Replace("projectName", projectInfo.Nameproperty);
                    slackBody = slackBody.Replace("versionNum", projectInfo.Revision);
                    string[] contentItem = slackBody.Split('#');
                    string slackEmoji = projectInfo.Result == "successful"
                        ? contentItem[1].Split(':')[0]
                        : contentItem[1].Split(':')[1];
                    contentItem[contentItem.Length - 2] = projectInfo.Result == "successful"
                        ? contentItem[contentItem.Length - 2].Split(':')[0]
                        : contentItem[contentItem.Length - 2].Split(':')[1];
                    slackBody = "";
                    for (int i = 2; i < contentItem.Length - 1; i++)
                    {
                        slackBody += contentItem[i];
                    }
                    string userName = projectInfo.SlackUser == "#author#" ? projectInfo.Author.Remove(0, 3)
                    : projectInfo.SlackUser;
                    //将传输信息写入json
                    var payLoad = new
                    {
                        channel = projectInfo.SlackChannel,
                        text = ":" + slackEmoji + ":" + slackBody,
                        username = userName
                    };
                    string json = JsonConvert.SerializeObject(payLoad);
                    string slackResult = webClient.UploadString(projectInfo.SlackUrl, json);
                }
            }
            catch (Exception e)
            {
                MessageBox.Show(e.Message);
            }
        }

        protected override void OnStop()
        {
        }
    }
}
