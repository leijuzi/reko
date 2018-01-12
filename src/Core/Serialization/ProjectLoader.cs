﻿#region License
/* 
 * Copyright (C) 1999-2017 John Källén.
 *
 * This program is free software; you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation; either version 2, or (at your option)
 * any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program; see the file COPYING.  If not, write to
 * the Free Software Foundation, 675 Mass Ave, Cambridge, MA 02139, USA.
 */
#endregion

using Reko.Core;
using Reko.Core.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Xml;
using System.Xml.Serialization;
using Reko.Core.Services;
using System.Diagnostics;
using System.Text;
using Reko.Core.Types;
using Reko.Core.Expressions;

namespace Reko.Core.Serialization
{
    /// <summary>
    /// Loads a Reko decompiler project file. May optionally ask the user
    /// for help.
    /// </summary>
    public class ProjectLoader : ProjectPersister
    {
        public event EventHandler<ProgramEventArgs> ProgramLoaded;

        private ILoader loader;
        private Project project;
        private IProcessorArchitecture arch;
        private IPlatform platform;
        private DecompilerEventListener listener;

        public ProjectLoader(IServiceProvider services, ILoader loader, DecompilerEventListener listener)
            : this(services, loader, new Project(), listener)
        {
        }

        public ProjectLoader(
            IServiceProvider services,
            ILoader loader,
            Project project,
            DecompilerEventListener listener)
            : base(services)
        {
            this.loader = loader;
            this.project = project;
            this.listener = listener;
        }

        /// <summary>
        /// Attempts to load the image as a project file.
        /// </summary>
        /// <param name="image"></param>
        /// <param name="loader"></param>
        /// <returns></returns>
        public Project LoadProject(string fileName, byte[] image)
        {
            if (!IsXmlFile(image))
                return null;
            try
            {
                Stream stm = new MemoryStream(image);
                return LoadProject(fileName, stm);
            }
            catch (XmlException)
            {
                return null;
            }
        }

        /// <summary>
        /// Peeks at the beginning of the image to determine if it's an XML file.
        /// </summary>
        /// <remarks>
        /// We do not attempt to handle UTF-8 encoded Unicode BOM characters.
        /// </remarks>
        /// <param name="image"></param>
        /// <returns></returns>
        private static bool IsXmlFile(byte[] image)
        {
            if (MemoryArea.CompareArrays(image, 0, new byte[] { 0x3C, 0x3F, 0x78, 0x6D, 0x6C }, 5)) // <?xml
                return true;
            return false;
        }

        public Project LoadProject(string filename)
        {
            var fsSvc = Services.RequireService<IFileSystemService>();
            using (var stm = fsSvc.CreateFileStream(filename, FileMode.Open, FileAccess.Read))
            {
                return LoadProject(filename, stm);
            }
        }

        private static readonly Tuple<Type, string>[] supportedProjectFileFormats =
        {
            Tuple.Create(typeof(Project_v4), SerializedLibrary.Namespace_v4),
            Tuple.Create(typeof(Project_v3), SerializedLibrary.Namespace_v3),
            Tuple.Create(typeof(Project_v2), SerializedLibrary.Namespace_v2),
        };

        /// <summary>
        /// Loads a .dcproject from a stream.
        /// </summary>
        /// <param name="stm"></param>
        /// <returns>
        /// The Project if the file format was recognized, otherwise null.
        /// </returns>
        public Project LoadProject(string filename, Stream stm)
        {
            var rdr = new XmlTextReader(stm);
            foreach (var fileFormat in supportedProjectFileFormats)
            {
                XmlSerializer ser = SerializedLibrary.CreateSerializer(fileFormat.Item1, fileFormat.Item2);
                if (ser.CanDeserialize(rdr))
                {
                    var deser = new Deserializer(this, filename);
                    return ((SerializedProject)ser.Deserialize(rdr)).Accept(deser);
                }
            }
            return null;
        }

        // Avoid reflection by using the visitor pattern.
        class Deserializer : ISerializedProjectVisitor<Project>
        {
            private ProjectLoader outer;
            private string filename;

            public Deserializer(ProjectLoader outer, string filename)
            {
                this.outer = outer; this.filename = filename;
            }
            public Project VisitProject_v2(Project_v2 sProject) { return outer.LoadProject(filename, sProject); }
            public Project VisitProject_v3(Project_v3 sProject) { return outer.LoadProject(filename, sProject); }
            public Project VisitProject_v4(Project_v4 sProject) { return outer.LoadProject(filename, sProject); }
        }

        /// <summary>
        /// Loads a Project object from its serialized representation. First loads the
        /// common architecture and platform then metadata, and finally any programs.
        /// </summary>
        /// <param name="sp"></param>
        /// <returns></returns>
        public Project LoadProject(string filename, Project_v4 sp)
        {
            var cfgSvc = Services.RequireService<IConfigurationService>();
            this.arch = cfgSvc.GetArchitecture(sp.ArchitectureName);
            if (arch == null)
                throw new ApplicationException(
                    string.Format("Unknown architecture '{0}' in project file.",
                        sp.ArchitectureName ?? "(null)"));
            var env = cfgSvc.GetEnvironment(sp.PlatformName);
            if (env == null)
                throw new ApplicationException(
                    string.Format("Unknown operating environment '{0}' in project file.",
                        sp.PlatformName ?? "(null)"));
            this.platform = env.Load(Services, arch);
            this.project.LoadedMetadata = this.platform.CreateMetadata();
            var typelibs = sp.Inputs.OfType<MetadataFile_v3>().Select(m => VisitMetadataFile(filename, m));
            var programs = sp.Inputs.OfType<DecompilerInput_v4>().Select(s => VisitInputFile(filename, s));
            sp.Inputs.OfType<AssemblerFile_v3>().Select(s => VisitAssemblerFile(s));
            project.MetadataFiles.AddRange(typelibs);
            project.Programs.AddRange(programs);
            return this.project;
        }

        /// <summary>
        /// Loads a Project object from its serialized representation. First loads the programs
        /// and then any extra metadata files.
        /// </summary>
        /// <param name="sp"></param>
        /// <returns></returns>
        public Project LoadProject(string filename, Project_v3 sp)
        {
            var programs = sp.Inputs.OfType<DecompilerInput_v3>().Select(s => VisitInputFile(filename, s)).ToList();
            var typelibs = sp.Inputs.OfType<MetadataFile_v3>().Select(m => VisitMetadataFile(filename, m)).ToList();
            sp.Inputs.OfType<AssemblerFile_v3>().Select(s => VisitAssemblerFile(s));
            this.project.LoadedMetadata = this.platform.CreateMetadata();
            foreach (var program in programs)
            {
                program.EnvironmentMetadata = this.project.LoadedMetadata;
            }
            project.Programs.AddRange(programs);
            project.MetadataFiles.AddRange(typelibs);
            return this.project;
        }

        /// <summary>
        /// Loads a Project object from its serialized representation. First loads the programs
        /// and then any extra metadata files.
        /// </summary>
        /// <param name="sp"></param>
        /// <returns></returns>
        public Project LoadProject(string projectFilePath, Project_v2 sp)
        {
            var typelibs = sp.Inputs.OfType<MetadataFile_v2>().Select(m => VisitMetadataFile(m));
            var programs = sp.Inputs.OfType<DecompilerInput_v2>().Select(s => VisitInputFile(projectFilePath, s)).ToList();
            sp.Inputs.OfType<AssemblerFile_v2>().Select(s => VisitAssemblerFile(s));
            if (this.platform != null)
            {
                this.project.LoadedMetadata = this.platform.CreateMetadata();
            }
            foreach (var program in programs)
            {
                program.EnvironmentMetadata = this.project.LoadedMetadata;
            }
            project.MetadataFiles.AddRange(typelibs);
            project.Programs.AddRange(programs);
            return this.project;
        }

        public Program VisitInputFile(string projectFilePath, DecompilerInput_v4 sInput)
        {
            var binAbsPath = ConvertToAbsolutePath(projectFilePath, sInput.Filename);
            var bytes = loader.LoadImageBytes(ConvertToAbsolutePath(projectFilePath, sInput.Filename), 0);
            var sUser = sInput.User;
            var address = LoadAddress(sUser, this.arch);
            Program program;
            if (address != null && 
                sUser.Processor != null &&
                (sUser.PlatformOptions == null ||
                sUser.PlatformOptions.Name != null))
            {
                var arch = sUser.Processor.Name;
                var platform = sUser.PlatformOptions != null
                    ? sUser.PlatformOptions.Name
                    : null;
                program = loader.LoadRawImage(binAbsPath, bytes, address, new LoadDetails
                {
                    LoaderName = sUser.Loader,
                    ArchitectureName = arch,
                    PlatformName = platform,
                    LoadAddress = sUser.LoadAddress,
                });
            }
            else
            {
                program = loader.LoadExecutable(binAbsPath, bytes, sUser.Loader, address);
            }
            program.Filename = binAbsPath;
            program.DisassemblyFilename = ConvertToAbsolutePath(projectFilePath, sInput.DisassemblyFilename);
            program.IntermediateFilename = ConvertToAbsolutePath(projectFilePath, sInput.IntermediateFilename);
            program.OutputFilename = ConvertToAbsolutePath(projectFilePath, sInput.OutputFilename);
            program.TypesFilename = ConvertToAbsolutePath(projectFilePath, sInput.TypesFilename);
            program.GlobalsFilename = ConvertToAbsolutePath(projectFilePath, sInput.GlobalsFilename);
            program.EnsureFilenames(program.Filename);
            LoadUserData(sUser, program, program.User);
            program.User.LoadAddress = address;
            ProgramLoaded.Fire(this, new ProgramEventArgs(program));
            return program;
        }


        public Program VisitInputFile(string projectFilePath, DecompilerInput_v3 sInput)
        {
            var binAbsPath = ConvertToAbsolutePath(projectFilePath, sInput.Filename);
            var bytes = loader.LoadImageBytes(binAbsPath, 0);
            var sUser = sInput.User;
            var address = LoadAddress(sUser);
            Program program;
            if (sUser.Processor != null && 
                (sUser.PlatformOptions == null ||
                sUser.PlatformOptions.Name != null))
            {
                var arch = sUser.Processor.Name;
                var platform = sUser.PlatformOptions != null
                    ? sUser.PlatformOptions.Name
                    : null;
                program = loader.LoadRawImage(binAbsPath, bytes, address, new LoadDetails
                {
                    ArchitectureName = arch,
                    PlatformName = platform,
                    LoadAddress = sUser.LoadAddress,
                });
            }
            else
            {
                program = loader.LoadExecutable(sInput.Filename, bytes, null, address);
            }
            this.platform = program.Platform;
            program.Filename = ConvertToAbsolutePath(projectFilePath, sInput.Filename);
            program.DisassemblyFilename = ConvertToAbsolutePath(projectFilePath, sInput.DisassemblyFilename);
            program.IntermediateFilename = ConvertToAbsolutePath(projectFilePath, sInput.IntermediateFilename);
            program.OutputFilename = ConvertToAbsolutePath(projectFilePath, sInput.OutputFilename);
            program.TypesFilename = ConvertToAbsolutePath(projectFilePath, sInput.TypesFilename);
            program.GlobalsFilename = ConvertToAbsolutePath(projectFilePath, sInput.GlobalsFilename);
            program.EnsureFilenames(program.Filename);
            LoadUserData(sUser, program, program.User);
            ProgramLoaded.Fire(this, new ProgramEventArgs(program));
            return program;
        }

        private Address LoadAddress(UserData_v4 user, IProcessorArchitecture arch)
        {
            if (user == null || user.LoadAddress == null)
                return null;
            Address addr;
            if (!arch.TryParseAddress(user.LoadAddress, out addr))
                return null;
            return addr;
        }

        private Address LoadAddress(UserData_v3 user)
        {
            if (user == null || user.LoadAddress == null || user.Processor == null)
                return null;
            Address addr;
            if (!Services.RequireService<IConfigurationService>()
                .GetArchitecture(user.Processor.Name)
                .TryParseAddress(user.LoadAddress, out addr))
                return null;
            return addr;
        }

        public void LoadUserData(UserData_v4 sUser, Program program, UserData user)
        {
            if (sUser == null)
                return;
            user.OnLoadedScript = sUser.OnLoadedScript;
            if (sUser.Processor != null)
            {
                program.User.Processor = sUser.Processor.Name;
                if (program.Architecture == null && !string.IsNullOrEmpty(program.User.Processor))
                {
                    program.Architecture = Services.RequireService<IConfigurationService>().GetArchitecture(program.User.Processor);
                }
                program.Architecture.LoadUserOptions(XmlOptions.LoadIntoDictionary(sUser.Processor.Options));
            }
            if (sUser.Procedures != null)
            {
                user.Procedures = sUser.Procedures
                    .Select(sup => LoadUserProcedure_v1(program, sup))
                    .Where(kv => kv.Key != null)
                    .ToSortedList(kv => kv.Key, kv => kv.Value);
            }

            if (sUser.PlatformOptions != null)
            {
                program.User.Environment = sUser.PlatformOptions.Name;
                program.Platform.LoadUserOptions(XmlOptions.LoadIntoDictionary(sUser.PlatformOptions.Options));
            }
            if (sUser.GlobalData != null)
            {
                user.Globals = sUser.GlobalData
                    .Select(sud =>
                    {
                        Address addr;
                        program.Architecture.TryParseAddress(sud.Address, out addr);
                        return new KeyValuePair<Address, GlobalDataItem_v2>(
                            addr,
                            sud);
                    })
                    .Where(kv => kv.Key != null)
                   .ToSortedList(kv => kv.Key, kv => kv.Value);
            }
          
            if (sUser.Heuristics != null)
            {
                user.Heuristics.UnionWith(sUser.Heuristics.Select(h => h.Name));
            }
            if (sUser.TextEncoding != null)
            {
                Encoding enc = null;
                try
                {
                    enc = Encoding.GetEncoding(sUser.TextEncoding);
                }
                catch
                {
                    var diagSvc = Services.RequireService<IDiagnosticsService>();
                    diagSvc.Warn(
                        "Unknown text encoding '{0}'. Defaulting to platform text encoding.", 
                        sUser.TextEncoding);
                }
                user.TextEncoding = enc;
            }
            program.EnvironmentMetadata = project.LoadedMetadata;
            if (sUser.Calls != null)
            {
                program.User.Calls = sUser.Calls
                    .Select(c => LoadUserCall(c, program))
                    .Where(c => c != null)
                    .ToSortedList(k => k.Address, v => v);
            }
            if (sUser.RegisterValues != null)
            {
                program.User.RegisterValues = LoadRegisterValues(sUser.RegisterValues);
            }
            if (sUser.JumpTables != null)
            {
                program.User.JumpTables = sUser.JumpTables.Select(LoadJumpTable_v4)
                    .Where(t => t != null)
                    .ToSortedList(k => k.Address, v => v);
            }
            if (user.IndirectJumps != null)
            {
                program.User.IndirectJumps = sUser.IndirectJumps
                    .Select(ij => LoadIndirectJump_v4(ij, program))
                    .Where(ij => ij != null)
                    .ToSortedList(k => k.Item1, v => v.Item2);
            }
        }

        private SortedList<Address, List<UserRegisterValue>> LoadRegisterValues(
            RegisterValue_v2[] sRegValues)
        {
            var allLists = new SortedList<Address, List<UserRegisterValue>>();
            foreach (var sRegValue in sRegValues)
            {
                Address addr;
                if (sRegValue != null && platform.TryParseAddress(sRegValue.Address, out addr))
                {
                    List<UserRegisterValue> list;
                    if (!allLists.TryGetValue(addr, out list))
                    {
                        list = new List<UserRegisterValue>();
                        allLists.Add(addr, list);
                    }
                    var reg = platform.Architecture.GetRegister(sRegValue.Register);
                    var c = Constant.Create(reg.DataType, Convert.ToUInt64(sRegValue.Value, 16));
                    if (reg != null)
                    {
                        list.Add(new UserRegisterValue
                        {
                            Register = reg,
                            Value = c
                        });
                    }
                }
            }
            return allLists;
        }

        private ImageMapVectorTable LoadJumpTable_v4(JumpTable_v4 sTable)
        {
            Address addr;
            if (!platform.TryParseAddress(sTable.TableAddress, out addr))
                return null;
            var listAddrDst = new List<Address>();
            foreach (var item in sTable.Destinations)
            {
                Address addrDst;
                if (!platform.TryParseAddress(item, out addrDst))
                    break;
                listAddrDst.Add(addrDst);
            }
            return new ImageMapVectorTable(addr, listAddrDst.ToArray(), 0);
        }

        private UserCallData LoadUserCall(SerializedCall_v1 call, Program program)
        {
            Address addr;
            if (!program.Platform.TryParseAddress(call.InstructionAddress, out addr))
                return null;

            var procSer = program.CreateProcedureSerializer();
            FunctionType sig = null;
            if (call.Signature != null)
            {
                sig = procSer.Deserialize(
                   call.Signature,
                   program.Architecture.CreateFrame());
            }
            return new UserCallData
            {
                Address = addr,
                Comment = call.Comment,
                NoReturn = call.NoReturn,
                Signature = sig,
            };
        }

        private Tuple<Address, UserIndirectJump> LoadIndirectJump_v4(IndirectJump_v4 indirJump, Program program)
        {
            Address addrInstr;
            if (!platform.TryParseAddress(indirJump.InstructionAddress, out addrInstr))
                return null;
            Address addrTable;
            if (!platform.TryParseAddress(indirJump.TableAddress, out addrTable))
                return null;
            ImageMapVectorTable table;
            if (!program.User.JumpTables.TryGetValue(addrTable, out table))
                return null;
            var reg = program.Architecture.GetRegister(indirJump.IndexRegister);
            if (reg == null)
                return null;
            return Tuple.Create(addrInstr, new UserIndirectJump
            {
                Address = addrInstr,
                Table = table,
                IndexRegister = reg,
            });
        }

        public void LoadUserData(UserData_v3 sUser, Program program, UserData user)
        {
            if (sUser == null)
                return;
            user.OnLoadedScript = sUser.OnLoadedScript;
            if (sUser.Processor != null)
            {
                program.User.Processor = sUser.Processor.Name;
                if (program.Architecture == null && !string.IsNullOrEmpty(program.User.Processor))
                {
                    program.Architecture = Services.RequireService<IConfigurationService>().GetArchitecture(program.User.Processor);
                }
                //program.Architecture.LoadUserOptions();       //$TODO
            }
            if (sUser.Procedures != null)
            {
                user.Procedures = sUser.Procedures
                    .Select(sup => LoadUserProcedure_v1(program, sup))
                    .Where(kv => kv.Key != null)
                    .ToSortedList(kv => kv.Key, kv => kv.Value);
            }

            if (sUser.PlatformOptions != null)
            {
                program.User.Environment = sUser.PlatformOptions.Name;
                program.Platform.LoadUserOptions(XmlOptions.LoadIntoDictionary(sUser.PlatformOptions.Options));
            }
            if (sUser.GlobalData != null)
            {
                user.Globals = sUser.GlobalData
                    .Select(sud =>
                    {
                        Address addr;
                        program.Architecture.TryParseAddress(sud.Address, out addr);
                        return new KeyValuePair<Address, GlobalDataItem_v2>(
                            addr,
                            sud);
                    })
                    .Where(kv => kv.Key != null)
                   .ToSortedList(kv => kv.Key, kv => kv.Value);
            }

            if (sUser.Heuristics != null)
            {
                user.Heuristics.UnionWith(sUser.Heuristics.Select(h => h.Name));
            }
            program.EnvironmentMetadata = project.LoadedMetadata;
        }

        private TypeLibraryDeserializer CreateTypeLibraryDeserializer()
        {
            return new TypeLibraryDeserializer(platform, true, project.LoadedMetadata.Clone());
        }

        public Program VisitInputFile(string projectFilePath, DecompilerInput_v2 sInput)
        {
            var binFilename = ConvertToAbsolutePath(projectFilePath, sInput.Filename);
            var bytes = loader.LoadImageBytes(binFilename, 0);
            var program = loader.LoadExecutable(binFilename, bytes, null, null);
            program.Filename = binFilename;
            this.platform = program.Platform;
            LoadUserData(sInput, program, program.User);

            program.DisassemblyFilename = sInput.DisassemblyFilename;
            program.IntermediateFilename = sInput.IntermediateFilename;
            program.OutputFilename = sInput.OutputFilename;
            program.TypesFilename = sInput.TypesFilename;
            program.GlobalsFilename = sInput.GlobalsFilename;
            program.EnsureFilenames(sInput.Filename);
            ProgramLoaded.Fire(this, new ProgramEventArgs(program));
            return program;
        }

        private void LoadUserData(DecompilerInput_v2 sInput, Program program, UserData user)
        {
            if (sInput.UserProcedures != null)
            {
                user.Procedures = sInput.UserProcedures
                        .Select(sup => LoadUserProcedure_v1(program, sup))
                        .Where(kv => kv.Key != null)
                        .ToSortedList(kv => kv.Key, kv => kv.Value);
            }
            if (sInput.UserGlobalData != null)
            {
                user.Globals = sInput.UserGlobalData
                    .Select(sud =>
                    {
                        Address addr;
                        program.Architecture.TryParseAddress(sud.Address, out addr);
                        return new KeyValuePair<Address, GlobalDataItem_v2>(
                            addr,
                            sud);
                    })
                    .Where(kv => kv.Key != null)
                   .ToSortedList(kv => kv.Key, kv => kv.Value);
            }
            user.OnLoadedScript = sInput.OnLoadedScript;
            if (sInput.Options != null)
            {
                program.User.Heuristics.Add("shingle");
            }
        }

        private KeyValuePair<Address, Procedure_v1> LoadUserProcedure_v1(
            Program program,
            Procedure_v1 sup)
        {
            Address addr;
            program.Architecture.TryParseAddress(sup.Address, out addr);
            if (!sup.Decompile && sup.Signature == null && string.IsNullOrEmpty(sup.CSignature))
            {
                listener.Warn(
                    listener.CreateAddressNavigator(program, addr),
                    "User procedure '{0}' has been marked 'no decompile' but its signature " +
                    "has not been specified.",
                    sup.Name);
            }
            return new KeyValuePair<Address, Procedure_v1>(addr, sup);
        }

        public MetadataFile VisitMetadataFile(string projectFilePath, MetadataFile_v3 sMetadata)
        {
            string filename = ConvertToAbsolutePath(projectFilePath, sMetadata.Filename);
            return LoadMetadataFile(filename);
        }

        public MetadataFile VisitMetadataFile(MetadataFile_v2 sMetadata)
        {
            string filename = sMetadata.Filename;
            return LoadMetadataFile(filename);
        }

        public MetadataFile LoadMetadataFile(string filename)
        {
            var platform = DeterminePlatform(filename);
            this.project.LoadedMetadata = loader.LoadMetadata(filename, platform, this.project.LoadedMetadata);
            return new MetadataFile
            {
                Filename = filename,
            };
        }

        private IPlatform DeterminePlatform(string filename)
        {
            // If a platform was defined for the whole project use that.
            if (this.platform != null)
                return this.platform;

            // Otherwise try to guess the platform or ask the user.
            // (this code will soon go away).
            var platformsInUse = project.Programs.Select(p => p.Platform).Distinct().ToArray();
            if (platformsInUse.Length == 1 && platformsInUse[0] != null)
                return platformsInUse[0];
            IPlatform platform = null;
            if (platformsInUse.Length == 0)
            {
                var oSvc = Services.GetService<IOracleService>();
                if (oSvc != null)
                {
                    platform = oSvc.QueryPlatform(string.Format(
                        "Please specify with operating environment should be used with metadata file {0}.",
                        filename));
                }
                Debug.Print("Got platform <{0}>", platform);
                return platform;
            }
            throw new NotImplementedException("Multiple platforms possible; not implemented yet.");
        }

        public Program VisitAssemblerFile(AssemblerFile_v3 sAsmFile)
        {
            return loader.AssembleExecutable(sAsmFile.Filename, sAsmFile.Assembler, null);
        }

        public Program VisitAssemblerFile(AssemblerFile_v2 sAsmFile)
        {
            return loader.AssembleExecutable(sAsmFile.Filename, sAsmFile.Assembler, null);
        }
    }

    public class ProgramEventArgs : EventArgs
    {
        public ProgramEventArgs(Program program)
        {
            this.Program = program;
        }

        public Program Program { get; private set; }
    }

    public class TypeLibraryEventArgs : EventArgs
    {
        public TypeLibraryEventArgs(TypeLibrary typelib)
        { 
            this.TypeLibrary = typelib; 
        }

        public TypeLibrary TypeLibrary { get; private set; }
    }
}
