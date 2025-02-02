(**
---
title: Builders and Evaluation
index: 6
---
*)

(*** condition: prepare ***)
#nowarn "211"
#r "nuget: FSharp.Data"
#r "../FsHttp/bin/Release/net5.0/FsHttp.dll"
open FsHttp
open FsHttp.DslCE


(**
## Lazy Evaluation / Chaining Builders

*Hint:* Have a look at: ```./src/FsHttp/DslCE.fs, module Fsi'```

There is not only the immediate + synchronous way of specifying requests. It's also possible to
simply build a request, pass it around and send it later or to warp it in async.

Chaining builders together: First, use a httpLazy to create a 'HeaderContext'

*Hint:* ```httpLazy { ... }``` is just a shortcut for ```httpRequest StartingContext { ... }```
*)
let postOnly =
    httpLazy {
        POST "https://reqres.in/api/users"
    }

(**
Add some HTTP headers to the context:
*)
let postWithCacheControlBut =
    postOnly {
        CacheControl "no-cache"
    }

(**
Transform the HeaderContext to a BodyContext and add JSON content:
*)
let finalPostWithBody =
    postWithCacheControlBut {
        body
        json """
        {
            "name": "morpheus",
            "job": "leader"
        }
        """
    }

(**
Finally, send the request (sync or async):
*)
let finalPostResponse = finalPostWithBody |> Request.send
let finalPostResponseAsync = finalPostWithBody |> Request.sendAsync



(**
### Async Builder

HTTP in an async context:
*)
let pageAsync =
    async {
        let! response = 
            httpAsync {
                GET "https://reqres.in/api/users?page=2&delay=3"
            }
        let page =
            response
            |> Response.toJson
            |> fun json -> json?page.AsInteger()
        return page
    }


// TODO Document naming conventions according to: https://github.com/fsprojects/FsHttp/issues/48

(**
## Naming Conventions

*Names for naming conventions according to: https://en.wikipedia.org/wiki/Naming_convention_(programming)#Lisp*

* Naming of **HTTP methods inside of a builder** are **upper flat case** (following https://tools.ietf.org/html/rfc7231#section-4).
    
    *Example:*
    ```fsharp
    http {
        GET "http://www.whatever.com"
    }
    ```

* Naming of **HTTP methods used outside of a builder** follow the F# naming convention and are **flat case**.

    *Example:*
    ```fsharp
    let request = get "http://www.whatever.com"
    ```

* Naming of **HTTP headers inside of a builder** are **PascalCase**. Even though they should be named **train case** (according to https://tools.ietf.org/html/rfc7231#section-5), it would require a double backtic using it in F#, which might be uncomfortable.

    *Example:*
    ```fsharp
    http {
        // ...
        CacheControl "no-cache"
    }
    ```

* Naming of **all other constructs** are **lower camel case**. This applies to:
    * config methods
    * type transformer (like "body")
    * content annotations (like "json" or "text")
    * FSI print modifiers like "expand" or "preview"
    * invocations like "send"

    *Example:*
    ```fsharp
    http {
        // ...
        timeoutInSeconds 10.0
        body
        json """ { ... } """
        expand
    }
    ```

## Examples for building, chaining and sending requests

*)

let getUsers1 : LazyHttpBuilder<HeaderContext> = get "https://reqres.in/api/users"
let getUsers2 : LazyHttpBuilder<HeaderContext> = httpLazy { GET "https://reqres.in/api/users" }
let _ : Response = getUsers1 { send }
let _ : Response = get "https://reqres.in/api/users" { send }
let _ : Response = getUsers1 |> Request.send
let _ : Response = http { GET "https://reqres.in/api/users" }
let _ : Async<Response> = httpAsync { GET "https://reqres.in/api/users" }
let _ : Response =
    httpLazy {
        GET "https://reqres.in/api/users"
        send
    }
let _ : Async<Response> =
    httpLazy {
        GET "https://reqres.in/api/users"
        sendAsync
    }

// FSI
let _ : Response =
    http {
        GET "https://reqres.in/api/users"
        CacheControl "no-cache"
        exp
    }

let _ : Response =
    get "https://reqres.in/api/users" {
        CacheControl "no-cache"
        exp
        send
    }

