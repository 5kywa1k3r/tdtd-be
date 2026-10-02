using System.Reflection;
using tdtd_be.Common.Errors;
using tdtd_be.Models;
using tdtd_be.Services.WorkAssignments.Handover;
using tdtd_be.Services.WorkAssignments.Internal;

internal static class HandoverScopeChecks
{
    internal static void Run()
    {
        var units = new[]{new Unit{Id="root"},new Unit{Id="own",ParentUnitId="root"},new Unit{Id="child",ParentUnitId="own"},
            new Unit{Id="grandchild",ParentUnitId="child"},new Unit{Id="peer",ParentUnitId="root"},
            new Unit{Id="virtual",ParentUnitId="own",IsVirtual=true},new Unit{Id="deleted",ParentUnitId="own",IsDeleted=true},
            new Unit{Id="spoof",Code="100001001",ParentUnitId="peer"}}.ToDictionary(x=>x.Id);
        foreach(var id in new[]{"own","child","grandchild"})
            if(!WorkAssignmentHandoverUnitScope.Allows("own",id,units)) throw new Exception("allowed unit hidden "+id);
        foreach(var id in new[]{"root","peer","virtual","deleted","spoof","missing"})
            if(WorkAssignmentHandoverUnitScope.Allows("own",id,units)) throw new Exception("outside unit accepted "+id);
        var validate=typeof(WorkAssignmentHandoverService).GetMethod("ValidateTransition",BindingFlags.NonPublic|BindingFlags.Static)!;
        foreach(var kind in new[]{"UNIT_MANAGER","NORMAL_USER"})
        {
            var from=new AppUser{Id="actor",Username="actor",AccountKind=kind,UnitId="child"};
            var originalReportingUnit=new UserRef{UserId="actor",UnitId="own"};
            foreach(var target in new[]{"child","grandchild","own","peer"})
            {
                try {
                    validate.Invoke(null,[from,new AppUser{Id="other",Username="other",AccountKind=kind,UnitId=target},originalReportingUnit,new UserRef{UserId="other",UnitId=target},units]);
                    if(target is "own" or "peer")throw new Exception("retained reporting unit widened handover");
                } catch(TargetInvocationException e) when(e.InnerException is AppException && target is "own" or "peer") { }
            }
        }
        Console.WriteLine("PASS 17 handover unit cases: own/descendant, reject ancestor/peer/virtual/deleted/spoof and retained reporting-unit escalation for normal/manager accounts");
    }
}
