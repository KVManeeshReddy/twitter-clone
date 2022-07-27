open Suave
open Suave.Http
open Suave.Operators
open Suave.Filters
open Suave.Successful
open Suave.Files
open Suave.RequestErrors
open Suave.Logging
open Suave.Utils
open FSharp.Json
open System
open System.Net

open Suave.Sockets
open Suave.Sockets.Control
open Suave.WebSocket
open System.Diagnostics
open System.Collections.Generic
open Akka.FSharp.Actors
open Akka.Actor
open Akka.FSharp.Spawn
open Suave.Json
open Newtonsoft.Json
open Suave.Writers

type clientRequest = 
    {
      userName:string
      value:string
      reqType:string
    }

type responseType = 
  {
    userName :string
    service : string
    message : string
    code : string
  } 

type ServerMessages = 
  |Register of string * string
  |AddSubscriber of string * string
  |MapSocket of string * WebSocket
  |Tweet of String * String 

let system = ActorSystem.Create("ServerEngine")
let mutable usersCred:Map<string,string> = Map.empty
let mutable wsMap:Map<string,WebSocket> = Map.empty
let mutable subscribersList:Map<String,String> = Map.empty
let mutable hashtagTweets:Map<String,String>= Map.empty
let mutable userMentionTweets:Map<String,String>=Map.empty

let sendMessage (ws:WebSocket)(message:String) = 
     let byteResponse =
      message
        |> System.Text.Encoding.ASCII.GetBytes
        |> ByteSegment
     ws.send Text byteResponse true |> Async.StartAsTask


let BossActor (mailbox: Actor<_>) =
    let rec loop() = actor {
        let! message = mailbox.Receive()
        match message with 
        |Register (userName,password) ->   usersCred <- Map.add userName password usersCred
                                           let mutable  temp = ""
                                           subscribersList <- Map.add userName temp subscribersList

        |AddSubscriber (user,follow) ->    let mutable  temp = subscribersList.[follow]
                                           temp <- temp + "," + user
                                           subscribersList <- Map.add follow temp subscribersList
                                           let message = "user " + user + " started following you"
                                           sendMessage wsMap.[follow] message |> ignore

        |Tweet (user,message) ->  let list = subscribersList.[user].Split(",")
                                  let mentionCheck = message.Split("@")
                                  if(mentionCheck.Length >1) then
                                    userMentionTweets <- Map.add mentionCheck[1] message userMentionTweets
                                    sendMessage wsMap.[mentionCheck[1] ] (user + " mentioned you : "+ message) |> ignore
                                  let hashtagCheck = message.Split("#")
                                  if(hashtagCheck.Length > 1) then
                                    hashtagTweets <- Map.add hashtagCheck[1] message hashtagTweets
                                  for i in list do
                                    let message = user + " Tweeted : " + message
                                    if(usersCred.ContainsKey(i)) then
                                      sendMessage wsMap.[i] message |> ignore
                                  sendMessage wsMap.[user] ("your tweet : " + message) |> ignore

        |MapSocket (user,ws) ->  wsMap <- Map.add user ws wsMap

        return! loop()
    }
    loop()




let bossActor = spawn system "BossActor" BossActor


let fromJson<'a> json =
        JsonConvert.DeserializeObject(json, typeof<'a>) :?> 'a


let ws (webSocket : WebSocket) (context: HttpContext) =
  socket {
    let mutable loop = true

    while loop do
      let! msg = webSocket.read()
      match msg with
      | (Text, data, true) ->
        let req = UTF8.toString data
        let response = sprintf "response to %s" req
        let reqObj = Json.deserialize<clientRequest> req
        bossActor <! MapSocket (reqObj.userName,webSocket)
        if(reqObj.reqType ="tweet") then
          bossActor <! Tweet (reqObj.userName,reqObj.value)
        let byteResponse =
          response
          |> System.Text.Encoding.ASCII.GetBytes
          |> ByteSegment

        byteResponse |> ignore
      | (Close, _, _) ->
        let emptyResponse = [||] |> ByteSegment
        do! webSocket.send Close emptyResponse true


      | _ -> ()
    }


let wsWithErrorHandling (webSocket : WebSocket) (context: HttpContext) = 
   
   let exampleDisposableResource = { new IDisposable with member __.Dispose() = printfn "Resource needed by websocket connection disposed" }
   let websocketWorkflow = ws webSocket context
   
   async {
    let! successOrError = websocketWorkflow
    match successOrError with
    | Choice1Of2() -> ()
    | Choice2Of2(error) ->
        printfn "Error: [%A]" error
        exampleDisposableResource.Dispose()
        
    return successOrError
   }


let getString (rawForm: byte[]) =
    System.Text.Encoding.UTF8.GetString(rawForm)


let userRegistration (input:clientRequest) =
    let mutable rectype : responseType = {userName ="userName"; service = "register";message="";code=""}
    if(usersCred.ContainsKey(input.userName)) then
        rectype <- {userName ="userName"; service = "register";message="User already registered";code="400"}
    else
        rectype <- {userName ="userName"; service = "register";message="User successfully registered";code="200"}
        bossActor <! Register (input.userName,input.value)
    rectype

let userLogin (input:clientRequest) =
    let mutable rectype : responseType = {userName ="userName"; service = "login";message="";code=""}
    if(usersCred.ContainsKey(input.userName) && usersCred.[input.userName]=input.value) then
        rectype <- {userName =input.userName; service = "login";message="User logged in successfully";code="200"}
    else
        rectype <- {userName =input.userName; service = "login";message="Invalid user name / password";code="400"}
    rectype

let subscriber (input:clientRequest) =
    let mutable rectype : responseType = {userName ="userName"; service = "subscribe";message="";code=""}
    if(usersCred.ContainsKey(input.value)) then
        bossActor <! AddSubscriber (input.userName,input.value)
        rectype <- {userName =input.userName; service = "subscribe";message="Subscribed to user "+input.value;code="200"}
    else
        rectype <- {userName =input.userName; service = "subscribe";message="No user found";code="400"}
    rectype

let runQuery (input:clientRequest) =
  let mutable rectype : responseType = {userName ="userName"; service = "query";message="";code=""}
  let queryCheck = input.value.Split("@")
  if(queryCheck.Length>1 && userMentionTweets.ContainsKey(queryCheck.[1])) then
      rectype <- {userName=input.userName;service="query";message="Query results : "+ userMentionTweets.[queryCheck.[1]];code="200"}
  
  else if(hashtagTweets.ContainsKey(input.value)) then
      rectype <- {userName=input.userName;service="query";message="Query results : "+ hashtagTweets.[input.value];code="200"}
  else
      rectype <- {userName=input.userName;service="query";message="No results found";code="200"}
  rectype
 

let registerUser =
    request (fun r ->
    r.rawForm
    |> getString
    |> fromJson
    |> userRegistration
    |> JsonConvert.SerializeObject
    |> CREATED )
    >=> setMimeType "application/json"

let loginUser =
    request (fun r ->
    r.rawForm
    |> getString
    |> fromJson
    |> userLogin
    |> JsonConvert.SerializeObject
    |> CREATED )
    >=> setMimeType "application/json"

let userQuery =
    request (fun r ->
    r.rawForm
    |> getString
    |> fromJson
    |> runQuery
    |> JsonConvert.SerializeObject
    |> CREATED )
    >=> setMimeType "application/json"

let userSubscribe =
    request (fun r ->
    r.rawForm
    |> getString
    |> fromJson
    |> subscriber
    |> JsonConvert.SerializeObject
    |> CREATED )
    >=> setMimeType "application/json"

let app : WebPart = 
  choose [
    path "/websocket" >=> handShake ws
    path "/websocketWithSubprotocol" >=> handShakeWithSubprotocol (chooseSubprotocol "test") ws
    path "/websocketWithError" >=> handShake wsWithErrorHandling
    GET >=> choose [ path "/" >=> file "register.html"; browseHome ]
    POST >=> choose [path "/register" >=> registerUser]
    POST >=> choose [path "/login" >=> loginUser]
    POST >=> choose [path "/query" >=> userQuery]
    POST >=> choose [path "/subscribe" >=> userSubscribe]
    NOT_FOUND "Found no handlers." ]

[<EntryPoint>]
let main _ =
  startWebServer { defaultConfig with logger = Targets.create Verbose [||] } app
  0