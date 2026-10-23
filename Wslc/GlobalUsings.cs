// wslc 模块原本使用 ImplicitUsings=enable；本仓库（wsl-manager）显式关闭了隐式 using，
// 这里为 Wslc 命名空间补回同等的一组，避免给每个搬过来的文件逐个加 using。
global using System;
global using System.Collections.Generic;
global using System.IO;
global using System.Linq;
global using System.Net.Http;
global using System.Text;
global using System.Threading;
global using System.Threading.Tasks;
