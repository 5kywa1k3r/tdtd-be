using tdtd_be.DTOs.AggregateMapping;
using tdtd_be.Services.AggregateMapping;

internal static class TextBlockPolicyChecks
{
    internal static void Run(Action<bool,string> check, Action<Action,string> error)
    {
        AggregateExpressionDto Input() => new() { Kind="INPUT", Ref="A" };
        AggregateExpressionDto Call(string name, AggregateFunctionOptionsDto? options=null) => new() { Kind="CALL", Name=name, Arguments=[Input()], Options=options };
        AggregateValue Text(string text,string? format=null)=>new("TEXT","VALUE",Text:text) {TextFormat=format};
        AggregateChannel Channel(params AggregateValue[] values)=>new("TEXT","SET",values.Select((v,i)=>new AggregateObservation(v,[new("r"+i,"u"+i,"ONCE","body")]) {
            SourceNote=new("","source"+i,"r"+i,"u"+i,"Tên đầy đủ "+i,"Báo cáo "+i,"ONCE","body",null,null) {
                UnitFullName="Tên đầy đủ "+i,UnitShortName=i==0?"Tên ngắn":"",UnitSymbol=i==0?"PV":"" }
        }).ToArray());
        AggregateValue Eval(AggregateExpressionDto e, AggregateChannel channel)=>AggregateEvaluator.Scalar(new AggregateEvaluator(new(default),[])
            .Evaluate(e,new Dictionary<string,AggregateChannel>{{"A",channel}})).Value;
        AggregateExpressionDto Search(string needle="x")=>new() { Kind="CALL",Name="TEXT_CONTAINS",Arguments=[Input(),new(){Kind="TEXT",Value=needle}] };
        var shortText=Text(new string('x',1000));var longText=Text(new string('x',1001));
        check(!AggregateTextPolicy.IsLong(shortText)&&AggregateTextPolicy.IsLong(longText),"R8 boundary 1000/1001 visible characters");
        check(Eval(Search(),Channel(shortText) with {Shape="SINGLE"}).Boolean==true,"R8 search short boundary allowed");
        error(()=>Eval(Search(),Channel(longText) with {Shape="SINGLE"}),"AGG_LONG_TEXT_CONCAT_REQUIRED");
        foreach(var name in new[]{"TEXT_STARTS","TEXT_ENDS","TEXT_EQUALS"})error(()=>Eval(Search() with {Name=name},Channel(longText) with {Shape="SINGLE"}),"AGG_LONG_TEXT_CONCAT_REQUIRED");
        foreach(var name in new[]{"LEN","TRIM"})error(()=>Eval(Call(name),Channel(longText) with {Shape="SINGLE"}),"AGG_LONG_TEXT_CONCAT_REQUIRED");
        error(()=>Eval(Call("COUNT_DISTINCT",new(){Trim=false,CaseSensitive=true}),Channel(shortText,longText)),"AGG_LONG_TEXT_CONCAT_REQUIRED");
        error(()=>Eval(new(){Kind="BINARY",Name="=",Arguments=[Input(),new(){Kind="TEXT",Value=longText.Text}]},Channel(longText) with {Shape="SINGLE"}),"AGG_LONG_TEXT_CONCAT_REQUIRED");
        check(!AggregateTextPolicy.IsLong(Text(string.Concat(Enumerable.Repeat("a\u0301",1000))))&&AggregateTextPolicy.IsLong(Text(string.Concat(Enumerable.Repeat("a\u0301",1001)))),"R8 combining accents count as visible graphemes");
        check(!AggregateTextPolicy.IsLong(Text(string.Concat(Enumerable.Repeat("👨‍👩‍👧‍👦",1000)))),"R8 joined emoji are one character each");
        check(!AggregateTextPolicy.IsLong(Text(new string('x',999)+"\r\n")),"R8 normalized CRLF counts once without trimming");
        var rich=Text("<p>"+string.Concat(Enumerable.Repeat("<b>x</b>",1000))+"</p>","RICH_HTML");
        check(!AggregateTextPolicy.IsLong(rich)&&Eval(Search(),Channel(rich) with {Shape="SINGLE"}).Boolean==true,"R8 counts visible HTML text, search strips formatting");
        error(()=>Eval(Call("COUNT_DISTINCT",new(){Trim=false,CaseSensitive=true}),Channel(rich)),"AGG_RICH_TEXT_DISTINCT_UNSUPPORTED");
        check(!AggregateTextPolicy.IsLong(Text("<p>"+string.Concat(Enumerable.Repeat("&amp;",1000))+"</p>","RICH_HTML")),"R8 decoded entities count once");
        check(Eval(Search("<b>"),Channel(Text("literal <b> text")) with {Shape="SINGLE"}).Boolean==true,"R8 plain angle brackets stay literal");
        var concat=Call("CONCAT",new(){Trim=false,Separator="|",Order="UNIT_THEN_PERIOD"});
        check(Eval(concat,Channel(longText,longText)).Text==longText.Text+"|"+longText.Text,"R8 long concatenation retains duplicate contributions");
        error(()=>Eval(Search(),Channel(Eval(concat,Channel(shortText,shortText))) with {Shape="SINGLE"}),"AGG_LONG_TEXT_CONCAT_REQUIRED");
        check(Eval(Call("COUNT",new(){Basis="REPORTS"}),Channel(longText,longText)).Number!.ToWire()=="2","R8 report counting is independent of text length");
        check(Eval(Search(),Channel(longText) with {Shape="SINGLE",TableOrigin=true}).Boolean==true,"R8 existing Table region contract unchanged");
        var block=Text("Tiếp nhận hồ sơ\nKiểm tra hiện trường",AggregateTextPolicy.StringListBlock);
        check(Eval(concat with {Options=new(){Trim=true,Separator="",Order="UNIT_THEN_PERIOD"}},Channel(block,block)).Text==block.Text+"\n\n"+block.Text,"R8 string-list whole blocks use system separation without dedup");
        error(()=>Eval(Search(),Channel(block) with {Shape="SINGLE"}),"AGG_TEXT_BLOCK_CONCAT_REQUIRED");
        error(()=>Eval(Call("COUNT_DISTINCT",new(){Trim=false,CaseSensitive=true}),Channel(block)),"AGG_TEXT_BLOCK_CONCAT_REQUIRED");
        error(()=>Eval(Call("LEN"),Channel(Eval(concat,Channel(block))) with {Shape="SINGLE"}),"AGG_TEXT_BLOCK_CONCAT_REQUIRED");
        foreach(var display in new[]{"FULL_NAME","SHORT_NAME","SYMBOL","NONE"})
        {
            var table=Eval(Call("REPORT_TEXT_TABLE",new(){Order="UNIT_THEN_PERIOD",UnitDisplay=display}),Channel(block,longText)).Table!;
            var expected=display switch {"FULL_NAME"=>"Tên đầy đủ 0","SHORT_NAME"=>"Tên ngắn","SYMBOL"=>"PV",_=>""};
            check(table.Rows[0][0].Value.Text==expected&&table.Rows[0][1].Value.Text==block.Text&&table.ContentRows![0].UnitId=="u0","R8 unit display "+display+" retains text and unit identity");
            if(display!="FULL_NAME")check(table.Rows[1][0].Value.Text=="","R8 missing selected unit label stays blank "+display);
        }
        error(()=>Eval(Call("REPORT_TEXT_TABLE",new(){Order="UNIT_THEN_PERIOD",UnitDisplay="invented"}),Channel(block)),"AGG_CONTENT_UNIT_DISPLAY");
    }
}
