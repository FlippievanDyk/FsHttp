module FsHttp.Request

open System
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Threading

open FsHttp.Domain

let private TimeoutPropertyName = "RequestTimeout"

let private setRequestMessageProp (requestMessage: HttpRequestMessage) (propName: string) (value: 'a) =
#if NETSTANDARD_2
    do requestMessage.Properties.[propName] <- value
#else
    do requestMessage.Options.Set(HttpRequestOptionsKey propName, value)
#endif

let private getRequestMessageProp<'a> (requestMessage: HttpRequestMessage) (propName: string) =
#if NETSTANDARD_2
    requestMessage.Properties.[propName] :?> 'a
#else
    match requestMessage.Options.TryGetValue<'a>(HttpRequestOptionsKey propName) with
    | true,value -> value
    | false,_ -> failwith $"HttpRequestOptionsKey '{propName}' not found."
#endif


/// Transforms a Request into a System.Net.Http.HttpRequestMessage.
let toMessage (request: Request): HttpRequestMessage =
    let header = request.header
    let requestMessage = new HttpRequestMessage(header.method, FsHttpUrl.toUriString header.url)
    do setRequestMessageProp requestMessage TimeoutPropertyName request.config.timeout

    let buildDotnetContent (part: ContentData) (contentType: string option) (name: string option) =
        let dotnetContent =
            match part with
            | StringContent s ->
                // TODO: Encoding is set hard to UTF8 - but the HTTP request has it's own encoding header.
                new StringContent(s) :> HttpContent
            | ByteArrayContent data -> new ByteArrayContent(data) :> HttpContent
            | StreamContent s -> new StreamContent(s) :> HttpContent
            | FormUrlEncodedContent data ->
                new FormUrlEncodedContent(data) :> HttpContent
            | FileContent path ->
                let content =
                    let fs = System.IO.File.OpenRead path
                    new StreamContent(fs)
                let contentDispoHeaderValue =
                    ContentDispositionHeaderValue("form-data")
                match name with
                | Some v -> contentDispoHeaderValue.Name <- v
                | None -> ()
                do
                    contentDispoHeaderValue.FileName <- path
                    content.Headers.ContentDisposition <- contentDispoHeaderValue
                content :> HttpContent
        if contentType.IsSome then
            dotnetContent.Headers.ContentType <- MediaTypeHeaderValue contentType.Value
        dotnetContent

    let assignContentHeaders (target: HttpHeaders) (headers: Map<string, string>) =
        for kvp in headers do
            target.Add(kvp.Key, kvp.Value)

    let dotnetContent =
        match request.content with
        | Empty -> null
        | Single bodyContent -> 
            let dotnetBodyContent = buildDotnetContent bodyContent.contentData bodyContent.contentType None
            do assignContentHeaders dotnetBodyContent.Headers bodyContent.headers
            dotnetBodyContent
        | Multi multipartContent ->
            let dotnetMultipartContent =
                match multipartContent.contentData with
                | [] -> null
                | contentPart ->
                    let dotnetPart = new MultipartFormDataContent()
                    for x in contentPart do
                        let dotnetContent = buildDotnetContent x.content x.contentType (Some x.name)
                        dotnetPart.Add(dotnetContent, x.name)
                    dotnetPart :> HttpContent
            do assignContentHeaders dotnetMultipartContent.Headers multipartContent.headers
            dotnetMultipartContent
    do
        requestMessage.Content <- dotnetContent
        assignContentHeaders requestMessage.Headers header.headers
    requestMessage

let private getHttpClient =
    let timeoutHandler innerHandler =
        { new DelegatingHandler(InnerHandler = innerHandler) with
            member _.SendAsync(request: HttpRequestMessage, cancellationToken: CancellationToken) =
                let cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                do cts.CancelAfter(getRequestMessageProp<TimeSpan> request TimeoutPropertyName)
                base.SendAsync(request, cts.Token)
        }

    fun (config: Config) ->
        match config.httpClientFactory with
        | Some clientFactory -> clientFactory()
        | None ->
            let transformHandler = Option.defaultValue id config.httpClientHandlerTransformer
            let handler =
                transformHandler <|
#if NETSTANDARD_2
                    new HttpClientHandler()
#else
                    new SocketsHttpHandler(UseCookies = false, PooledConnectionLifetime = TimeSpan.FromMinutes 5.0)
#endif

            match config.certErrorStrategy with
            | Default -> ()
            | AlwaysAccept ->
#if NETSTANDARD_2
                handler.ServerCertificateCustomValidationCallback <- (fun msg cert chain errors -> true)
#else
                handler.SslOptions <-
                    let options = Security.SslClientAuthenticationOptions()
                    options.RemoteCertificateValidationCallback <-
                        Security.RemoteCertificateValidationCallback(fun sender cert chain errors -> true)
                    options
#endif

            match config.proxy with
            | Some proxy ->
                let webProxy = WebProxy(proxy.url)
                match proxy.credentials with
                | Some cred ->
                    webProxy.UseDefaultCredentials <- false
                    webProxy.Credentials <- cred
                | None -> webProxy.UseDefaultCredentials <- true
                handler.Proxy <- webProxy
            | None -> ()

            new HttpClient(handler |> timeoutHandler, Timeout = Timeout.InfiniteTimeSpan)

/// Builds an asynchronous request, without sending it.
let buildAsync (context: IToRequest) =
    async {
        let request = context.ToRequest()
        if request.config.printptDebugMessages then
            printfn $"Sending request {request.header.method} {FsHttpUrl.toUriString request.header.url} ..."
        use requestMessage = toMessage request
        use finalRequestMessage = 
            let httpMessageTransformer = Option.defaultValue id request.config.httpMessageTransformer
            httpMessageTransformer requestMessage
        let! ctok = Async.CancellationToken
        let client = getHttpClient request.config
        let cookies =
            request.header.cookies
            |> List.map string
            |> String.concat "; "
        do finalRequestMessage.Headers.Add("Cookie", cookies)
        let finalClient = 
            let httpClientTransformer = Option.defaultValue id request.config.httpClientTransformer
            httpClientTransformer client
        let! response =
            finalClient.SendAsync(finalRequestMessage, request.config.httpCompletionOption, ctok)
            |> Async.AwaitTask
        if request.config.printptDebugMessages then
            printfn $"{Helper.HttpStatusCode.show response.StatusCode} ({request.header.method} {FsHttpUrl.toUriString request.header.url})"
        let dispose () =
            do finalClient.Dispose()
            do response.Dispose()
        return { request = request
                 content = response.Content
                 headers = response.Headers
                 reasonPhrase = response.ReasonPhrase
                 statusCode = response.StatusCode
                 requestMessage = response.RequestMessage
                 version = response.Version
                 originalHttpRequestMessage = requestMessage
                 originalHttpResponseMessage = response
                 dispose = dispose }
    }

/// Sends a context asynchronously.
let sendAsync (context: IToRequest) =
    context
    |> buildAsync
    |> Async.StartChild
    |> Async.RunSynchronously

/// Sends a context synchronously.
let inline send context =
    context
    |> buildAsync
    |> Async.RunSynchronously
