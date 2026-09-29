# Design Requirements

This is the document you design against.

Your task after reading is Stage 1: the blueprint, a high-level design of how this system will work. Do not jump to code yet.

## About this project

This is a real software system that was built for and used in production by a real company in the healthcare industry. 

Customers paid to use it, and real clinical trial data flowed through it.

You will design, build and host it, and you get feedback at each stage.

Once your blueprint solution is ready, post it here to get feedback: [https://www.skool.com/devmastery?c=1d489d4c8b4f4e19aab2455b80986d9a](https://www.skool.com/devmastery?c=1d489d4c8b4f4e19aab2455b80986d9a)

## Business context

The client builds software for managing clinical trial documentation. Their core product is an electronic document management system: a highly structured, permission-controlled file store used by research teams across hospitals and biotech companies.

Documents are organized into a hierarchy:

* Team  
  * Binder (a document collection)  
    * Folder (optional: can be nested)  
      * Document

A user only sees and acts on the parts of this hierarchy they are authorized to access.

## The problem being solved

Clinical trial coordinators routinely need to upload the same set of documents to many destinations at once.

For example, 10 regulatory documents that must land in 15 different team binders across 5 hospitals. One file at a time, one destination at a time, is slow and error-prone.

The client needs a tool, that lets a user:

* Select multiple files in a single session  
* Map each file to one or more destinations (teams, binders, folders)  
* Submit the whole batch in one action  
* Track the progress of every individual upload  
* Know exactly which uploads succeeded and which failed

DocBridge is a standalone web application that delivers files into the document platform through its API.

## Functional requirements

1\. Destination discovery. An authenticated user can browse the destinations available to them (teams, binders, and nested folders). The list must reflect the user's real permissions; they only see and upload to what they are authorized to access.

2\. File selection and destination mapping. A user can select multiple files in one session and assign each file to one or more destinations. One file can go to many destinations; one destination can receive many files.

3\. Job submission. Submitting creates a job that represents the whole batch. The system acknowledges submission immediately; the user does not wait for uploads to finish. Users can see their active and past jobs.

4\. Asynchronous processing. Each file-to-destination pair is a task; a job is made of one or more tasks. Tasks are processed asynchronously and independently, so one task failing must not block or cancel the others. Processing is decoupled from submission.

5\. Status tracking. Each task has a lifecycle: pending, in-progress, completed, failed. The job reflects an aggregate status derived from its tasks. Users can query a job and see every task's status, and statuses update automatically as work progresses. (Pending means the task was created and the message is sitting in the queue waiting to be picked up. No worker has touched it yet. In-progress means a worker pulled the message from the queue and is executing. The queue no longer holds it; a worker owns it.)

6\. File delivery. Each task ultimately uploads a file to the client's document platform via its API. The file must arrive intact (integrity verified), and delivery failures must be handled gracefully and surfaced on the task with enough detail to understand what went wrong.

7\. Authentication and authorization. Every operation runs on behalf of an authenticated user. The system must manage users and credentials itself, this includes login and token issuance. Authorization is resource-based: a user's access is determined by which teams they are a member of, and they can only see and act on resources that belong to those teams.

8\. File retrieval. A user can download any file belonging to a job submitted by a member of their teams. Downloads must not flow through application servers.

## Non-functional requirements

**Scale**

| Dimension | Target |
| :---- | :---- |
| Active users | 2,000 registered users; up to 200 concurrent during business hours |
| Files per batch | Up to 100 files per job submission |
| Destinations per batch | Up to 20 destinations per job |
| Tasks per job (files × destinations) | up to 2000 tasks in a single operation |
| Daily deliveries | 90,000 tasks/day |
| Average file size | 2 MB |
| Maximum file size | 1 GB |
| Supported file types | PDF, DOCX, XLSX, PNG, JPG (no executables) |

**Availability**  
The system must maintain 99.9% uptime (less than 9 hours of unplanned downtime per year).

**Performance**

* Job submission acknowledged in under 500 ms at p99, regardless of the number of files or destinations in the batch.  
* Destination browser (teams, binders, folders) loads in under 1 second for a user with access to up to 50 teams.  
* The UI must remain responsive during file selection and upload; it must never freeze or block the user.

**Reliability**

* No uploaded file may be silently lost. Delivery failure rate must not exceed 0.1% under normal operating conditions, and every failure must be recorded and surfaced to the user with enough detail to understand what went wrong.  
* Transient delivery failures (e.g. the destination API is temporarily unavailable) must be retried automatically. The system must attempt delivery at least 3 times before marking a task as failed.  
* Partial failure is expected and acceptable: if some tasks in a job fail, the rest must still complete.  
* A failed task must not affect any other task in the same job.

**Security**

* All files must be encrypted at rest. Encryption must be managed by a dedicated cloud key management service; the system must not implement its own cryptography.  
* File integrity must be verifiable end to end. The system must detect any corruption between upload and delivery.  
* Files must never be accessible to any party other than the submitting user's authorized team members at any point in the pipeline.  
* No credentials, API keys, or secrets may appear in source code. All secrets must be injected at runtime via environment variables or a secrets manager.  
* All API endpoints must require authentication. Unauthenticated requests must be rejected.  
* For compliance, staged files are retained for 30 days after job completion, then deleted by lifecycle policy

**Operability**

* Deployment to cloud infrastructure must be automated; no manual steps beyond initial credentials setup.  
* All configuration (database URLs, AWS credentials, service endpoints, feature flags) must come from environment variables or a secrets manager.  
* Application services (API, auth, workers, document platform) must be runnable locally via docker compose against emulated or dev cloud resources.

## Scope for v1

Don't over-engineer the blueprint, here is what is out of scope for the first version:

* Observability. Default cloud metrics and logs are enough for v1. No custom dashboards, tracing, or alerting yet.  
* Everything must be built from scratch. There are no external services to integrate with: you build the authentication service, the document management platform, and the file distribution system.  
* Focus your design on the core pipeline: select and map files, submit a job, stage files securely, process tasks asynchronously, deliver to the destination, and track per-task status.

## Your task: Stage 1, the blueprint

Produce a high-level system design, including:

* The major components and how they interact (a diagram or a clear written description)  
* The datastores you will use, and why  
* How a file moves through the system from upload to final delivery  
* How you handle asynchronous processing  
* How you address the security requirements  
* Anything you are uncertain about or need to make a decision on

A strong blueprint should show why each piece exists, how you keep submission instant, how tasks process independently, how nothing gets silently lost, and how files stay protected end to end.

When you have made a genuine attempt, post it here for review: [https://www.skool.com/devmastery?c=1d489d4c8b4f4e19aab2455b80986d9a](https://www.skool.com/devmastery?c=1d489d4c8b4f4e19aab2455b80986d9a)

