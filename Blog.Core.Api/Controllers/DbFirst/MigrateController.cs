﻿using Blog.Core.Common.Helper;
using Blog.Core.IRepository.UnitOfWork;
using Blog.Core.IServices;
using Blog.Core.Model;
using Blog.Core.Model.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Blog.Core.Controllers
{
    [Route("api/[controller]/[action]")]
    [ApiController]
    //[Authorize(Permissions.Name)]
    public class MigrateController : ControllerBase
    {
        private readonly IUnitOfWork _unitOfWork;
        private readonly IRoleModulePermissionServices _roleModulePermissionServices;
        private readonly IUserRoleServices _userRoleServices;
        private readonly IRoleServices _roleServices;
        private readonly IPermissionServices _permissionServices;
        private readonly IModuleServices _moduleServices;
        private readonly IWebHostEnvironment _env;

        public MigrateController(IUnitOfWork unitOfWork,
            IRoleModulePermissionServices roleModulePermissionServices,
            IUserRoleServices userRoleServices,
            IRoleServices roleServices,
            IPermissionServices permissionServices,
            IModuleServices moduleServices,
            IWebHostEnvironment env)
        {
            _unitOfWork = unitOfWork;
            _roleModulePermissionServices = roleModulePermissionServices;
            _userRoleServices = userRoleServices;
            _roleServices = roleServices;
            _permissionServices = permissionServices;
            _moduleServices = moduleServices;
            _env = env;
        }

        private void InitPermissionTree(List<Permission> permissions, List<Permission> all, List<Modules> apis)
        {
            foreach (var item in permissions)
            {
                item.Children = all.Where(d => d.Pid == item.Id).ToList();
                item.Module = apis.FirstOrDefault(d => d.Id == item.Mid);
                InitPermissionTree(item.Children, all, apis);
            }
        }

        private async Task SavePermissionTreeAsync(List<Permission> permissions, List<PM> pms, int permissionId = 0)
        {
            var parendId = permissionId;

            foreach (var item in permissions)
            {
                PM pm = new PM();
                // 保留原始主键id
                pm.PidOld = item.Id;
                pm.MidOld = (item.Module?.Id).ObjToInt();

                var mid = 0;
                // 接口
                if (item.Module != null)
                {
                    var moduleModel = (await _moduleServices.Query(d => d.LinkUrl == item.Module.LinkUrl)).FirstOrDefault();
                    if (moduleModel != null)
                    {
                        mid = moduleModel.Id;
                    }
                    else
                    {
                        mid = await _moduleServices.Add(item.Module);
                    }
                    pm.MidNew = mid;
                    Console.WriteLine($"Moudle Added:{item.Module.Name}");
                }
                // 菜单
                if (item != null)
                {
                    item.Pid = parendId;
                    item.Mid = mid;
                    permissionId = await _permissionServices.Add(item);
                    pm.PidNew = permissionId;
                    Console.WriteLine($"Permission Added:{item.Name}");
                }
                pms.Add(pm);

                await SavePermissionTreeAsync(item.Children, pms, permissionId);
            }
        }

        /// <summary>
        /// 获取权限部分Map数据（从库）
        /// 迁移到新库（主库）
        /// </summary>
        /// <returns></returns>
        [HttpGet]
        public async Task<MessageModel<string>> DataMigrateFromOld2New()
        {
            var data = new MessageModel<string>() { success = true, msg = "" };
            if (_env.IsDevelopment())
            {
                try
                {
                    var apiList = await _moduleServices.Query(d => d.IsDeleted == false);
                    var permissionsList = await _permissionServices.Query(d => d.IsDeleted == false);
                    var permissions = permissionsList.Where(d => d.Pid == 0).ToList();
                    var rmps = await _roleModulePermissionServices.GetRMPMaps();
                    List<PM> pms = new List<PM>();

                    // 当然，你可以做个where查询
                    rmps = rmps.Where(d => d.PermissionId >= 114).ToList();

                    InitPermissionTree(permissions, permissionsList, apiList);

                    permissions = permissions.Where(d => d.Id >= 114).ToList();

                    // 开启事务，保证数据一致性
                    _unitOfWork.BeginTran();

                    await SavePermissionTreeAsync(permissions, pms);

                    var rid = 0;
                    var pid = 0;
                    var mid = 0;
                    var rpmid = 0;

                    // 注意信息的完整性，不要重复添加，确保主库没有要添加的数据
                    foreach (var item in rmps)
                    {
                        // 角色信息，防止重复添加，做了判断
                        if (item.Role != null)
                        {
                            var isExit = (await _roleServices.Query(d => d.Name == item.Role.Name && d.IsDeleted == false)).FirstOrDefault();
                            if (isExit == null)
                            {
                                rid = await _roleServices.Add(item.Role);
                                Console.WriteLine($"Role Added:{item.Role.Name}");
                            }
                            else
                            {
                                rid = isExit.Id;
                            }
                        }

                        pid = (pms.FirstOrDefault(d => d.PidOld == item.PermissionId)?.PidNew).ObjToInt();
                        mid = (pms.FirstOrDefault(d => d.MidOld == item.ModuleId)?.MidNew).ObjToInt();
                        // 关系
                        if (rid > 0 && pid > 0)
                        {
                            rpmid = await _roleModulePermissionServices.Add(new RoleModulePermission()
                            {
                                IsDeleted = false,
                                CreateTime = DateTime.Now,
                                ModifyTime = DateTime.Now,
                                ModuleId = mid,
                                PermissionId = pid,
                                RoleId = rid,
                            });
                            Console.WriteLine($"RMP Added:{rpmid}");
                        }

                    }


                    _unitOfWork.CommitTran();

                    data.success = true;
                    data.msg = "导入成功！";
                }
                catch (Exception)
                {
                    _unitOfWork.RollbackTran();

                }
            }
            else
            {
                data.success = false;
                data.msg = "当前不处于开发模式，代码生成不可用！";
            }

            return data;
        }


        /// <summary>
        /// 权限数据库导出tsv
        /// </summary>
        /// <returns></returns>
        [HttpGet]
        public async Task<MessageModel<string>> SaveData2TsvAsync()
        {
            var data = new MessageModel<string>() { success = true, msg = "" };
            if (_env.IsDevelopment())
            {

                JsonSerializerSettings microsoftDateFormatSettings = new JsonSerializerSettings
                {
                    DateFormatHandling = DateFormatHandling.MicrosoftDateFormat
                };

                // 取出数据，序列化，自己可以处理判空
                var rolesJson = JsonConvert.SerializeObject(await _roleServices.Query(d => d.IsDeleted == false), microsoftDateFormatSettings);
                FileHelper.WriteFile(Path.Combine(_env.WebRootPath, "BlogCore.Data.json", "Role_New.tsv"), rolesJson, Encoding.UTF8);


                var permissionsJson = JsonConvert.SerializeObject(await _permissionServices.Query(d => d.IsDeleted == false), microsoftDateFormatSettings);
                FileHelper.WriteFile(Path.Combine(_env.WebRootPath, "BlogCore.Data.json", "Permission_New.tsv"), permissionsJson, Encoding.UTF8);


                var modulesJson = JsonConvert.SerializeObject(await _moduleServices.Query(d => d.IsDeleted == false), microsoftDateFormatSettings);
                FileHelper.WriteFile(Path.Combine(_env.WebRootPath, "BlogCore.Data.json", "Modules_New.tsv"), modulesJson, Encoding.UTF8);


                var rmpsJson = JsonConvert.SerializeObject(await _roleModulePermissionServices.Query(d => d.IsDeleted == false), microsoftDateFormatSettings);
                FileHelper.WriteFile(Path.Combine(_env.WebRootPath, "BlogCore.Data.json", "RoleModulePermission_New.tsv"), rmpsJson, Encoding.UTF8);



                data.success = true;
                data.msg = "生成成功！";
            }
            else
            {
                data.success = false;
                data.msg = "当前不处于开发模式，代码生成不可用！";
            }

            return data;
        }

    }

    public class PM
    {
        public int PidOld { get; set; }
        public int MidOld { get; set; }
        public int PidNew { get; set; }
        public int MidNew { get; set; }
    }
}
