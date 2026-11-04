namespace BotPlus.Openings.Core;

public static class HoleWorkflow
{
    static void Editable(Hole hole,Actor actor)
    {
        if(actor.Role==Role.Viewer)throw new InvalidOperationException("Пользователю не назначена рабочая роль.");
        if(hole.Imported)throw new InvalidOperationException("Задание получено из другой модели. Измените исходный реестр и обновите задания.");
        if(hole.Deleted)throw new InvalidOperationException("Отверстие удалено.");
    }
    static void Allowed(Actor actor,params Role[] roles)
    {if(actor.Role!=Role.Admin&&!roles.Contains(actor.Role))throw new InvalidOperationException("Действие недоступно для текущей роли.");}
    static void Reason(string text){if(string.IsNullOrWhiteSpace(text))throw new InvalidOperationException("Укажите причину действия.");}
    public static void Log(Hole hole,Actor actor,string action,string comment="")
    {hole.History.Add(new(DateTimeOffset.UtcNow,actor.User,actor.Role,action,comment,hole.Revision,hole.Geometry,actor.ActingFor));}
    static void Reset(Hole h){h.Approvals.Clear();h.ApprovalAnchor=null;h.Rejected=false;}
    public static Hole Create(HoleProject project,Actor actor,HoleGeometry geometry,string discipline,string levelId,string levelName,double levelElevation,string mark="")
    {
        Allowed(actor,Role.Engineer);geometry.Validate();
        if(string.IsNullOrWhiteSpace(discipline))throw new InvalidOperationException("Укажите раздел.");
        var hole=new Hole{ProjectId=project.ProjectId,CreatedBy=actor.Owner,Geometry=geometry,Discipline=discipline.Trim(),LevelId=levelId,LevelName=levelName,LevelElevation=levelElevation,Mark=mark};
        Log(hole,actor,"Создано новое отверстие");return hole;
    }
    public static void Approve(Hole h,Actor actor,Role role)
    {
        Editable(h,actor);Allowed(actor,role);
        if(role is not (Role.Engineer or Role.Architect or Role.Structural)||h.DeletionRequested||h.GeometryUnresolved)throw new InvalidOperationException("Согласование сейчас недоступно.");
        if(h.Approvals.Any(a=>a.Role==role))return;
        h.ApprovalAnchor??=h.Geometry;h.Approvals.Add(new(role,actor.User,DateTimeOffset.UtcNow));h.Rejected=false;h.Revision++;
        Log(h,actor,"Согласовано: "+Names.Role(role));
    }
    public static void Unapprove(Hole h,Actor actor,Role role)
    {
        Editable(h,actor);Allowed(actor,role);
        if(h.DeletionRequested)throw new InvalidOperationException("Сначала отмените запрос удаления.");
        if(h.Approvals.RemoveAll(a=>a.Role==role)==0)return;
        if(h.Approvals.Count==0)h.ApprovalAnchor=null;h.Revision++;Log(h,actor,"Снято согласование: "+Names.Role(role));
    }
    public static void ApproveArKr(Hole h,Actor actor,HoleProject project,string reason)
    {Allowed(actor,Role.Architect);if(!project.AllowArchitectStructuralApproval)throw new InvalidOperationException("Согласование АР за КР не разрешено в настройках проекта.");Reason(reason);Approve(h,actor,Role.Architect);
        if(!h.Approvals.Any(a=>a.Role==Role.Structural)){h.Approvals.Add(new(Role.Structural,actor.User,DateTimeOffset.UtcNow));h.Revision++;Log(h,actor,"АР согласовал за КР",reason);}}
    public static void Reject(Hole h,Actor actor,string reason)
    {Editable(h,actor);Allowed(actor,Role.Architect,Role.Structural);Reason(reason);if(h.DeletionRequested)throw new InvalidOperationException("Сначала отмените запрос удаления.");Reset(h);h.Rejected=true;h.Revision++;Log(h,actor,"Отказ",reason);}
    public static void Comment(Hole h,Actor actor,string comment)
    {Editable(h,actor);Reason(comment);h.Revision++;Log(h,actor,"Комментарий",comment);}
    public static void Change(Hole h,Actor actor,HoleGeometry geometry,Tolerance tolerance,string reason)
    {
        Editable(h,actor);if(h.DeletionRequested)throw new InvalidOperationException("Сначала отмените запрос удаления.");geometry.Validate();tolerance.Validate();
        if(h.Geometry==geometry&&!h.GeometryUnresolved)return;
        bool invalidate=h.GeometryUnresolved||h.ApprovalAnchor is {} anchor&&!tolerance.Contains(anchor,geometry);
        if(invalidate&&h.Approvals.Count>0)Reason(reason);
        var before=h.Geometry;h.Geometry=geometry;h.GeometryUnresolved=false;h.Rejected=false;if(invalidate)Reset(h);h.Revision++;
        string size=before.Shape==HoleShape.Round?$"Ø{before.Diameter:0.#}":$"{before.Width:0.#} × {before.Height:0.#}";
        Log(h,actor,invalidate?"Геометрия изменена; согласования сняты":"Геометрия изменена",reason+$"\nБыло: {size} мм; глубина {before.Depth:0.#} мм; центр {before.X:0.#}; {before.Y:0.#}; {before.Z:0.#} мм; угол {before.Yaw*180/Math.PI:0.#}°.");
    }
    public static void MarkExternalChange(Hole h,Actor actor,string reason)
    {if(h.Deleted||h.GeometryUnresolved)return;Reset(h);h.GeometryUnresolved=true;h.Revision++;Log(h,actor,"Изменение средствами Revit; требуется проверить геометрию",reason);}
    public static void RequestDelete(Hole h,Actor actor)
    {
        Editable(h,actor);Allowed(actor,Role.Engineer);
        if(actor.Role!=Role.Admin&&!h.CreatedBy.Equals(actor.Owner,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Удаление доступно создателю или назначенному заместителю.");
        if(h.DeletionRequested)return;
        h.DeleteRequired=h.Approvals.Where(a=>a.Role is Role.Architect or Role.Structural).Select(a=>a.Role).Distinct().ToList();h.DeleteAllowed.Clear();
        if(actor.Role==Role.Admin||h.DeleteRequired.Count==0){h.Deleted=true;h.DeletionRequested=false;h.Revision++;Log(h,actor,"Удалено");}
        else{h.DeletionRequested=true;h.Revision++;Log(h,actor,"Запрошено удаление");}
    }
    public static void AllowDelete(Hole h,Actor actor,Role role)
    {
        Editable(h,actor);Allowed(actor,role);
        if(!h.DeletionRequested||!h.DeleteRequired.Contains(role))throw new InvalidOperationException("Разрешение этой роли на удаление не требуется.");
        if(h.DeleteAllowed.Contains(role))return;h.DeleteAllowed.Add(role);h.Revision++;Log(h,actor,"Разрешено удаление: "+Names.Role(role));
        if(h.DeleteRequired.All(h.DeleteAllowed.Contains)){h.Deleted=true;h.DeletionRequested=false;Log(h,actor,"Удалено после согласования");}
    }
    public static void CancelDelete(Hole h,Actor actor)
    {Editable(h,actor);Allowed(actor,Role.Engineer);if(actor.Role!=Role.Admin&&!h.CreatedBy.Equals(actor.Owner,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Отменить удаление может создатель или заместитель.");
        if(!h.DeletionRequested)return;h.DeletionRequested=false;h.DeleteAllowed.Clear();h.DeleteRequired.Clear();h.Revision++;Log(h,actor,"Удаление отменено");}
    public static Hole Copy(HoleProject project,Hole source,Actor actor,double dx,double dy,double dz,string levelId,string levelName,double levelElevation)
    {
        var h=Create(project,actor,source.Geometry with{X=source.Geometry.X+dx,Y=source.Geometry.Y+dy,Z=source.Geometry.Z+dz},source.Discipline,levelId,levelName,levelElevation);
        h.Assignment=source.Assignment;Log(h,actor,"Скопировано из "+source.Id);return h;
    }
    public static Hole Merge(HoleProject project,IReadOnlyList<Hole> holes,Actor actor,bool replaceSources)
    {
        if(holes.Count<2||holes.Select(h=>h.Key).Distinct().Count()!=holes.Count)throw new InvalidOperationException("Выберите не менее двух разных отверстий.");
        foreach(var h in holes)Editable(h,actor);var first=holes[0];double yaw=first.Geometry.Yaw,c=Math.Cos(yaw),s=Math.Sin(yaw);
        if(holes.Any(h=>h.Geometry.Orientation!=first.Geometry.Orientation||Math.Abs(Math.Sin(h.Geometry.Yaw-yaw))>1e-7||h.LevelId!=first.LevelId||h.HostId!=first.HostId||h.HostLinkId!=first.HostLinkId))throw new InvalidOperationException("Объединяются отверстия одной ориентации, уровня и основы.");
        var points=holes.SelectMany(h=>h.Geometry.Corners()).Select(p=>first.Geometry.Orientation==HoleOrientation.Wall?new[]{c*p[0]+s*p[1],p[2],-s*p[0]+c*p[1]}:new[]{c*p[0]+s*p[1],-s*p[0]+c*p[1],p[2]}).ToList();
        var min=Enumerable.Range(0,3).Select(i=>points.Min(p=>p[i])).ToArray();var max=Enumerable.Range(0,3).Select(i=>points.Max(p=>p[i])).ToArray();var mid=Enumerable.Range(0,3).Select(i=>(min[i]+max[i])/2).ToArray();
        var geometry=first.Geometry with{Shape=HoleShape.Rectangle,Width=max[0]-min[0],Height=max[1]-min[1],Depth=max[2]-min[2],
            X=c*mid[0]-s*(first.Geometry.Orientation==HoleOrientation.Wall?mid[2]:mid[1]),Y=s*mid[0]+c*(first.Geometry.Orientation==HoleOrientation.Wall?mid[2]:mid[1]),Z=first.Geometry.Orientation==HoleOrientation.Wall?mid[1]:mid[2]};
        Allowed(actor,Role.Engineer,Role.Architect,Role.Structural);geometry.Validate();
        var merged=new Hole{ProjectId=project.ProjectId,CreatedBy=actor.Owner,Geometry=geometry,Discipline=first.Discipline,Assignment=first.Assignment,LevelId=first.LevelId,LevelName=first.LevelName,LevelElevation=first.LevelElevation,HostId=first.HostId,HostLinkId=first.HostLinkId};
        merged.MergedFrom=holes.Select(h=>new MergedHistory(h.Key,HoleJson.Clone(h.History))).Concat(holes.SelectMany(h=>h.MergedFrom)).DistinctBy(h=>h.Id).ToList();
        Log(merged,actor,"Создано объединённое отверстие",string.Join(", ",holes.Select(h=>h.Id)));
        if(replaceSources)foreach(var h in holes){h.Deleted=true;h.DeletionRequested=false;h.Revision++;Log(h,actor,"Заменено объединённым отверстием",merged.Id);}
        return merged;
    }
    public static void ArchiveHistory(Hole h,Actor actor)
    {Editable(h,actor);Allowed(actor,Role.Admin);h.HistoryStart=h.History.Count;h.Revision++;Log(h,actor,"История перенесена в архив; прежние события сохранены");}
}
