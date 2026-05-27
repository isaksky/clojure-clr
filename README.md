# Branch Summary

This branch is primarily interesting for the startup speed change in the
installed `clojure.main` global tool path. The comparison below uses the actual
global-tool shims on `PATH`: this branch installed as `clj-mayne`, and the
existing `clojure.main` package command `Clojure.Main`.

The measurements were taken on 2026-05-26 MDT on macOS 15.7.3 arm64 with .NET
SDK `10.0.107` and runtime `10.0.7`. Each row uses one warmup and three measured
fresh processes.

`dotnet tool list --global` showed both packages at `1.12.3-alpha8`. On disk,
`clj-mayne` had 48 compiled namespaces prepared as ReadyToRun images, including
compiled spec namespaces. `Clojure.Main` had 45 IL compiled namespaces and still
source-loaded spec.

| Startup probe | `clj-mayne` median / p95 | `Clojure.Main` median / p95 | Result |
| --- | ---: | ---: | --- |
| `-e "(println :ok)"` | `174.5 ms` / `193.7 ms` | `676.0 ms` / `681.6 ms` | `3.9x` faster |
| First `clojure.string` require | `185.7 ms` / `199.5 ms` | `697.5 ms` / `713.7 ms` | `3.8x` faster |
| `clojure.spec.alpha` validation | `223.7 ms` / `244.8 ms` | `697.0 ms` / `701.8 ms` | `3.1x` faster |
| `samples/stm/teststm.clj` | `219.4 ms` / `233.1 ms` | `695.8 ms` / `752.6 ms` | `3.2x` faster |
| `samples/spec_schema.clj` | `247.8 ms` / `267.8 ms` | `904.6 ms` / `937.5 ms` | `3.7x` faster |
| `samples/newtonsoft_demo.cljr` | `245.4 ms` / `245.7 ms` | Fails: missing `Newtonsoft.Json.Linq.JObject` | branch completes |
| `samples/sqlite_demo.cljr` | `266.1 ms` / `274.8 ms` | Fails: missing `Microsoft.Data.Sqlite.SqliteConnection` | branch completes |

The observed branch global-tool range was `173.6-274.8 ms`; the existing
`Clojure.Main` global tool took `651.6-937.5 ms` on comparable successful probes
and failed the two external-package script probes.

# ClojureCLR

This project is a native implementation of Clojure on the Common Language Runtime (CLR),
the execution engine of Microsoft's .Net Framework.

ClojureCLR is programmed in C# (and Clojure itself).

## Goals

* Implement a feature-complete Clojure on top of CLR
* Stay as close as possible to the JVM implementation
* Have some fun

## Getting Started

ClojureCLR can either run as a a standalone application, or embedded as a library in .NET applications. See the
[getting started](https://github.com/clojure/clojure-clr/wiki/Getting-started) guide for instructions on how to
install, run or integrate ClojureCLR.

## Documentation

Most of the documentation for [Clojure](https://clojure.org/) should be equally applicable to ClojureCLR. See the
ClojureCLR [wiki]((https://github.com/clojure/clojure-clr/wiki)) and the articles under the [docs](/docs) directory
in this repository for documentation specific to the ClojureCLR project.

## Community and Support

* The [#clr](https://clojurians.slack.com/archives/C060SFCPR) channel in the [Clojurians Slack](https://clojurians.slack.com)
* The [ClojureCLR](https://ask.clojure.org/index.php/clojureclr) category in [Ask Clojure](https://ask.clojure.org/)
* [ClojureCLR JIRA](https://clojure.atlassian.net/jira/software/c/projects/CLJCLR/issues/) is used for issue tracking.
Note that creating issues in the Clojure JIRA requires signing a
[contributor agreement](https://clojure.org/dev/contributor_agreement). Minor issues might be easier to raise on Slack.

## Libraries

Many of the standard libraries from JVM Clojure have [CLR ports](https://github.com/orgs/clojure/repositories?q=clr).

## Other Resources

* [David Miller and Clojure on the CLR](https://zencastr.com/z/oOS93SBX) with [@dmiller](https://github.com/dmiller/)
* [Transform Microsoft Office Solutions into Cloud-savvy Linked Data Microservices With Clojure on .NET](https://www.youtube.com/watch?v=pImaXoTPWWA) with [@bcalco](https://github.com/bcalco/)

## License ##

    Copyright (c) Rich Hickey. All rights reserved. The use and
    distribution terms for this software are covered by the Eclipse
    Public License 1.0 (http://opensource.org/licenses/eclipse-1.0.php)
    which can be found in the file epl-v10.html at the root of this
    distribution. By using this software in any fashion, you are
    agreeing to be bound by the terms of this license. You must
    not remove this notice, or any other, from this software.
