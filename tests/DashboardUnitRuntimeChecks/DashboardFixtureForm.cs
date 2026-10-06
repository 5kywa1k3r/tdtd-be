using System.Text.Json;
using MongoDB.Bson;
using tdtd_be.Models;
using tdtd_be.Services.DynamicForms;

// The dashboard's period-count fixture still needs a real immutable Form pin.
internal static class DashboardFixtureForm
{
    internal static DynamicFormTemplate Create(string id,string run,string owner,string? familyId=null)
    {
        var form=new DynamicFormTemplate{Id=id,FamilyId=familyId??ObjectId.GenerateNewId().ToString(),VersionNo=1,
            Name="[THỬ RIÊNG DASHBOARD] Chỉ tiêu",Code=run+"-form",CreatedByUserId=owner,CreatedByUsername=run,Note=run,IsPublished=true,
            SectionsJson="[{\"id\":\"main\",\"title\":\"Chỉ tiêu thử\",\"order\":0,\"tagCodes\":[]}]",
            FieldsJson=JsonSerializer.Serialize(new[]{new{id="value",key="value",name="Giá trị thử",label="Giá trị thử",sectionId="main",type="number",required=false}})};
        _=DynamicFormSectionSnapshotBuilder.Build(form);
        var snapshot=DynamicFormPublishedSchemaSnapshotBuilder.Build(form);
        form.PublishedSchemaSnapshotJson=snapshot.Json;form.PublishedSchemaHash=snapshot.Sha256;
        DynamicFormPublishedSchemaSnapshotBuilder.ValidateAgainstTemplate(form);
        return form;
    }
}
