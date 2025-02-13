module Funogram.Tools

open JsonConverters
open Newtonsoft.Json
open Newtonsoft.Json.Serialization
open System
open System.Net.Http
open System.Reflection
open System.Runtime.CompilerServices
open System.Text

[<assembly:InternalsVisibleTo("Funogram.Tests")>]
do ()

open Types

let private getUrl (config: BotConfig) methodName = 
    sprintf "%s%s/%s" (config.TelegramServerUrl |> string) config.Token methodName
let internal getUnix (date: DateTime) = 
    Convert.ToInt64(date.Subtract(DateTime(1970, 1, 1)).TotalSeconds)

let private jsonOpts = 
    JsonSerializerSettings
        (NullValueHandling = NullValueHandling.Ignore, 
         ContractResolver = DefaultContractResolver
                                (NamingStrategy = SnakeCaseNamingStrategy()), 
         Converters = [| OptionConverter()
                         DuConverter() |], 
         ConstructorHandling = ConstructorHandling.AllowNonPublicDefaultConstructor)

let internal parseJson<'a> str = 
    match (JsonConvert.DeserializeObject<Types.ApiResponse<'a>>(str, jsonOpts)) with
    | x when x.Ok && x.Result.IsSome -> Ok x.Result.Value
    | x when x.Description.IsSome && x.ErrorCode.IsSome -> 
        Error { Description = x.Description.Value
                ErrorCode = x.ErrorCode.Value }
    | _ -> 
        Error { Description = "Unknown error"
                ErrorCode = -1 }

let toJsonString (o: 'a) = JsonConvert.SerializeObject(o, jsonOpts)

let internal parseModeName parseMode = 
    match parseMode with
    | None -> None
    | _ -> 
        match parseMode.Value with
        | HTML -> Some "HTML"
        | Markdown -> Some "Markdown"

let internal getChatIdString (chatId: Types.ChatId) = 
    match chatId with
    | Int v -> v |> string
    | String v -> v

let internal getChatIdStringOption (chatId: Types.ChatId option) = 
    chatId
    |> Option.map getChatIdString
    |> Option.defaultValue ""

let private isOption (t: Type) = 
    t.GetTypeInfo().IsGenericType 
    && t.GetGenericTypeDefinition() = typedefof<option<_>>

let internal (|SomeObj|_|) = 
    let ty = typedefof<option<_>>
    fun (a: obj) -> 
        let aty = a.GetType().GetTypeInfo()
        let v = aty.GetProperty("Value")
        if aty.IsGenericType && aty.GetGenericTypeDefinition() = ty then 
            if isNull (a) then None
            else Some(v.GetValue(a, [||]))
        else None

// let private clientLazy  =  lazy ( new HttpClient())

[<AbstractClass>]
type internal Api private () = 
    // static member private Client  = clientLazy.Value
    
    static member private ConvertParameterValue(value: obj): HttpContent * string option = 
        let typeInfo = value.GetType().GetTypeInfo()
        if value :? bool then 
            (new StringContent(value.ToString().ToLower()) :> HttpContent, None)
        elif value :? string then 
            (new StringContent(value :?> string) :> HttpContent, None)
        elif value :? DateTime then 
            let date = value :?> DateTime
            (new StringContent(getUnix date |> string) :> HttpContent, None)
        elif typeInfo.IsPrimitive then 
            (new StringContent(value.ToString(), Encoding.UTF8) :> HttpContent, 
             None)
        elif (value :? Types.FileToSend) then 
            let vl = value :?> Types.FileToSend
            match vl with
            | Types.Url x -> 
                (new StringContent(x.ToString()) :> HttpContent, None)
            | Types.FileId x -> (new StringContent(x) :> HttpContent, None)
            | Types.File(name, content) -> 
                (new StreamContent(content) :> HttpContent, Some name)
        else (new StringContent(toJsonString value) :> HttpContent, None)
    
    static member private DowncastOptionObj = 
        let ty = typedefof<option<_>>
        fun (a: obj) -> 
            let aty = a.GetType().GetTypeInfo()
            let v = aty.GetProperty("Value")
            if aty.IsGenericType && aty.GetGenericTypeDefinition() = ty then 
                if isNull (a) then None
                else Some(v.GetValue(a, [||]))
            else None
    
    static member internal MakeRequestAsync<'a>(config: BotConfig,
                                                methodName: string, 
                                                ?param: (string * obj) list) = 
        async {
            let client = config.Client

            let url = getUrl config methodName
            if param.IsNone || param.Value.Length = 0 then
                let! jsonString = client.GetStringAsync(url) |> Async.AwaitTask 
                return jsonString |> parseJson<'a>
            else 
                let paramValues = 
                    param.Value 
                    |> List.choose (
                        fun (key, value) -> 
                        match value with
                        | null -> None
                        | SomeObj(o) -> Some(key, o)
                        | _ -> 
                            if isOption (value.GetType()) then 
                                None
                            else Some(key, value))

                if paramValues 
                   |> Seq.exists (fun (_, b) -> (b :? Types.FileToSend)) then 
                    use form = new MultipartFormDataContent()
                    paramValues 
                    |> Seq.iter (
                        fun (name, value) -> 
                        let content, fileName = Api.ConvertParameterValue(value)
                        
                        if fileName.IsSome then form.Add (content, name, fileName.Value)
                        else form.Add(content, name))
                        
                    let! result = 
                        client.PostAsync(url, form)
                        |> Async.AwaitTask
                        
                    let! jsonString = result.Content.ReadAsStringAsync() |> Async.AwaitTask
                    return parseJson<'a> jsonString
                else 
                    let json = toJsonString (paramValues |> dict)
                    let result = 
                        new StringContent(json, Encoding.UTF8, 
                                          "application/json")
                    
                    let! result = 
                        client.PostAsync(url, result)
                        |> Async.AwaitTask
                        
                    let! jsonString = result.Content.ReadAsStringAsync() 
                                      |> Async.AwaitTask 
                        
                    return parseJson<'a> jsonString
        }