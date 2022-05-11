﻿using MSAddinTest.Core.Executor;
using MSAddinTest.MSTestInterface;
using MSAddinTest.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;

namespace MSAddinTest.Core.Loader
{
    /// <summary>
    /// 程序集动态加载
    /// </summary>
    public partial class PluginAssemblyLoader
    {
        public LoaderSetup Setup { get; private set; }

        /// <summary>
        /// 初始化
        /// </summary>
        /// <param name="pluginName"></param>
        public PluginAssemblyLoader(LoaderSetup pluginDomainSetup)
        {
            Setup = pluginDomainSetup;

            // 监听事件
            RegistryEvents();

            // 获取所有 dll 文件
            _allFileNames = Directory.GetFiles(Setup.BaseDirectory, "*.dll", SearchOption.AllDirectories).ToList();
            _allFileNames.AddRange(Directory.GetFiles(AppDomain.CurrentDomain.BaseDirectory, "*.dll", SearchOption.AllDirectories).ToList());

            _autoReloader = new AutoReloader(this);
        }

        // 自动重新加载
        private readonly AutoReloader _autoReloader;

        // 所有根目录下的 dll 文件
        private List<string> _allFileNames;

        // 从程序集中读取的执行器
        private List<ExecutorBase> _executors = new List<ExecutorBase>();

        private string _lastFileHash = "";
        public FuncResult LoadAssembly()
        {
            try
            {
                // 验证文件 hash 值
                var newFileHash = FileHelper.GetFileHash(Setup.DllFullPath);
                if (_lastFileHash == newFileHash) 
                    return new FuncResult(true);
                else
                    _lastFileHash = newFileHash;


                // 读取文件然后加载
                byte[] bytes = File.ReadAllBytes(Setup.DllFullPath);
                var assembly = Assembly.Load(bytes);

                var results = BuilderExecutors(assembly);
                _executors = results.ToList();

                return new FuncResult(true);
            }
            catch (Exception ex)
            {
                return new FuncResult(false, ex.Message + ex.StackTrace);
            }
        }



        /// <summary>
        /// 生成执行器
        /// </summary>
        /// <returns></returns>
        protected virtual IEnumerable<ExecutorBase> BuilderExecutors(Assembly assembly)
        {
            var results = new List<ExecutorBase>();

            // 获取类执行器
            results.AddRange(GenerateClassExecutor(assembly));

            // 静态方法执行器
            results.AddRange(GenerateStaticMethodExecutor(assembly));

            // 添加 addin 执行器
            results.AddRange(GenerateAddinExecutor(assembly));

            return results;
        }

        // 获取类执行器
        private IEnumerable<ExecutorBase> GenerateClassExecutor(Assembly assembly)
        {
            List<ExecutorBase> results = new List<ExecutorBase>();

            // 生成运行数据
            var iPluginType = typeof(IMSTest_Class);
            var pluginTypes = assembly.GetTypes().Where(x => !x.IsInterface && !x.IsAbstract && iPluginType.IsAssignableFrom(x));
            // 获取非 addin 插件
            {
                var commonPluginTypes = pluginTypes;
                foreach (var pluginType in commonPluginTypes)
                {
                    var classExecutor = new ClassExecutor(pluginType);
                    results.Add(classExecutor);
                }
            }

            return results;
        }

        // 读取静态执行器
        // 条件要求：
        // 1-继承接口 IMSTest_StaticMethod
        // 2-具有 MSTestAttribute 属性
        // 3-有一个 string 类型的参数
        private IEnumerable<ExecutorBase> GenerateStaticMethodExecutor(Assembly assembly)
        {
            List<ExecutorBase> results = new List<ExecutorBase>();

            // 生成运行数据
            var iPluginType = typeof(IMSTest_StaticMethod);
            var pluginTypes = assembly.GetTypes().Where(x => !x.IsInterface && !x.IsAbstract && iPluginType.IsAssignableFrom(x));
            // 获取静态方法执行器
            {
                var commonPluginTypes = pluginTypes;
                foreach (var pluginType in commonPluginTypes)
                {
                    var methodInfos = pluginType.GetMethods().Where(x => x.GetCustomAttribute(typeof(MSTestAttribute)) != null);
                    foreach (var methodInfo in methodInfos)
                    {
                        // 获取参数
                        var paraInfos = methodInfo.GetParameters();
                        if (paraInfos.Length != 1 || paraInfos[0].ParameterType.Equals(typeof(string))) continue;

                        var classExecutor = new StaticMethodExecutor(methodInfo);

                        results.Add(classExecutor);
                    }
                }
            }

            return results;
        }

        // 读取addin执行器
        private IEnumerable<ExecutorBase> GenerateAddinExecutor(Assembly assembly)
        {
            // 生成运行数据
            var iPluginType = typeof(MSTest_Addin);
            var allTypes = assembly.GetTypes();
            var pluginTypes = allTypes.Where(x => x.IsSubclassOf(iPluginType));

            foreach (var pluginType in pluginTypes)
            {
                // 找到后立即进行初始化
                try
                {
                    var addin = Activator.CreateInstance(pluginType, IntPtr.Zero) as MSTest_Addin;
                    // 进行初始化
                    addin.Init(Index.MSAddin.Instance);
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show("Addin初始化失败：" + ex.Message);
                }
            }

            // 开始读取命令表
            var resourceNames = assembly.GetManifestResourceNames().Where(x => x.EndsWith(".xml"));

            var results = new List<ExecutorBase>();

            // 读取所有的命令表
            foreach (var resourceName in resourceNames)
            {
                var xmlStream = assembly.GetManifestResourceStream(resourceName);
                var xDoc = XElement.Load(xmlStream);
                XNamespace ns = "http://www.bentley.com/schemas/1.0/MicroStation/AddIn/KeyinTree.xsd";
                var childerens = xDoc.Descendants(ns + "KeyinHandler");
                // 获取属性
                foreach (XElement xElement in childerens)
                {
                    var keyin = xElement.Attribute("Keyin").Value;
                    var function = xElement.Attribute("Function").Value;

                    // 通过这两个参数生成执行器
                    var lastIndex = function.LastIndexOf(".");
                    var fullTypeName = function.Substring(0, lastIndex);
                    var functionName = function.Substring(lastIndex + 1);

                    var functionType = allTypes.FirstOrDefault(x => x.FullName == fullTypeName);
                    if (functionType == null) continue;

                    var addinExecutor = new AddinExecutor(functionType, functionName);
                    addinExecutor.Names.Add(keyin);

                    results.Add(addinExecutor);
                }
            }

            // 通过命令表查到静态方法
            return results;
        }
    }
}
