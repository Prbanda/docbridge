**DocBridge Blueprint: PAS-02**

**Status:** Draft  
**Owner:** Pranathi Banda  
**Updated:** 27 July 2026  
 

**Business context**

The client builds software for managing clinical trial documentation. Their core product is an electronic document management system: a highly structured, permission-controlled file store used by research teams across hospitals and biotech companies.  
   
Documents are organized into a hierarchy:

* Team  
  * Binder (a document collection)  
    * Folder (optional: can be nested)  
      * Document

A user only sees and acts on the parts of this hierarchy they are authorized to access. 

**What problem we're solving and why**

Clinical trial coordinators routinely need to upload the same set of documents to many destinations at once. Users should be able to upload multiple documents to multiple destinations without blocking the user from doing other activities.

For example, 10 regulatory documents that must land in 15 different team binders across 5 hospitals. One file at a time, one destination at a time, is slow and error prone.

The client needs a tool that lets a user:

* Select multiple files in a single session  
* Map each file to one or more destinations (teams, binders, folders)  
* Submit the whole batch in one action  
* Track the progress of every individual upload  
* Know exactly which uploads succeeded and which failed


DocBridge is a standalone web application that delivers files into the document platform through its API.  
   
**Metrics:**

·       The system must maintain 99.9% uptime.  
·       Job submission is acknowledged in under 500ms at p99, regardless of number of files or destinations.  
·       UI to remain responsive during file selection and upload; it must never freeze or block the user.

**Current State:** Documents are maintained in local servers with no proper hierarchy which makes document upload and retrieval difficult and repetitive.

**Impact:** Maintaining and retrieving the documents is difficult if the number of files is in thousands.

**Context**

Background information is needed to understand this project. Links to relevant documentation, previous discussions, or related systems.

**Key Terminologies**

| Term | Definition |
| :---- | :---- |
| DocBridge | The file distribution application. It allows users to upload multiple files, map them to multiple destinations, and monitor delivery progress.  |
| Document Platform  | The destination document management system where files are ultimately delivered and stored. It manages Teams, Binders, Folders, and Documents. |
| User | An authenticated individual authorized to access one or more Teams within the Document Platform.  |
| Team | The highest level organizational unit that groups users and controls access to document resources. A user may belong to multiple teams  |
| Binder | A logical collection of related documents within a Team. Each Binder contains one or more folders and documents.  |
| Folder | A hierarchical container inside a Binder used to organize documents. Folders may contain nested subfolders.  |
| Destination | Any valid upload target selected by the user. A destination consists of a Team, Binder, and optionally a Folder. |
| Document | The final file is stored in the Document Platform after successful processing. Supports PDF, DOCX, XLSX, PNG, JPG |
| Staged File | A temporarily stored file in Blob Storage awaiting asynchronous delivery to the Document Platform.  |
| Job Status | Status of the file uploaded. Should be one of the following. Pending, In-Progress, Completed, Failed. |

 

**Related Documentation**

* Design Requirements:  [https\://app.eraser.io/workspace/slW88DuXXSAUnj7zIvQ5](https://app.eraser.io/workspace/slW88DuXXSAUnj7zIvQ5)  
* High level Design: [https\://app.eraser.io/workspace/sfhybhy1wU1GlELnna2g?origin=share](https://app.eraser.io/workspace/sfhybhy1wU1GlELnna2g?origin=share)   
* ![][image1]

   


**Assumptions**

* Maximum upload size is 1GB  
* Average file size 2MB  
* Supported file types PDF, DOCX, XLSX, PNG, JPG (no executables)

**Job Stories**

**Upload:**

**1\.**       **When** user is trying to upload a document**, I want** to identify the destinations where the user has write permission**, so that** user is allowed to select only those folders.  
**2\.**       **When** user uploads a document to a folder, **I want** to successfully store the document in a database or any storage, **so that** no data is lost and should be able to successfully retrieve and view the document.  
**3\.**       **When a user** uploads files**, I want** to create a job where all the documents are queued**, so that** the uploading process is done asynchronously and update the status of the jobs in real-time. 

**Download:**

**1\.**       **When the user** opens the Audit trail, **I want** to show the files that have been uploaded recently and display the options ‘download as CSV’ or ‘download as PDF’ options, **so that the user** can download the files based on the access permission.

**Status:**

**1\.**       **When a user** uploads documents to folder(s), **I want** to see progress of all the jobs that are uploading, **so that the user** can take appropriate actions based on the result.  
**2\.**       **When a user** wants to view the uploaded files, **I want** to show all the file details that are uploaded, **so that** I can see who uploaded the document, the timestamp of the upload and the destination path.

**High-Level Architecture**  
[**https\://app.eraser.io/workspace/sfhybhy1wU1GlELnna2g?origin=share**](https://app.eraser.io/workspace/sfhybhy1wU1GlELnna2g?origin=share)

**Component Overview:**

| Component | Responsibility | Tech stack |
| :---- | :---- | :---- |
| **User Interface** | User authentication, Browse accessible teams, binders and folders | React, Typescript, Vite, Material UI |
| **API Gateway** | Single entry point for all client requests, Route requests to appropriate backend service, Apply rate limiting, forward authentication requests.  Provide one public endpoint while hiding internal services. | ASP.NET Core Web API (.NET)  \+ YARP  |
| **Load Balancer** | Distribute incoming requests across API interfaces. Prevents any single API instance from becoming overloaded. | AWS Application load balancer |
| **Authentication service** | Takes care of user registration, login, password hashing, JWT generation, Token validation, Team membership lookup. | ASP.NET Core Web API \+ JWT   \+ BCrypt |
| **DocBridge API** | Core orchestration service. Receives upload requests, validates requests, upload files to blob storage, publish tasks to task queue, return job ID immediately, update job and task status, generate secure download URLs. | ASP.NET Core Web API, Entity Framework Core  |
| **Platform API** | Manage Teams, Binders and folder hierarchy. Accepts uploaded documents, stores final document metadata. Enforces destination permissions. | ASP.NET Core Web API, Entity Framework Core  |
| **Blob Storage** | Stores uploaded files, Encrypt files at rest, Provide direct downloads through pre-signed URLs | AWS S3 |
| **Task Queue** | Decouple upload from processing, Hold one message per File-Destination task, Buffer workload spikes | Amazon SQS |
| **Job Worker** | Consumes task messages, retrieves staged file, verify checksum, upload document to Doc platform, update task status, Update aggregate job status, send permanently failed tasks to Dead Letter Queue | .NET Worker Service(BackgroundService ) |
| **Dead Letter Queue** | Stores tasks that failed after all retry attempts. | Amazon SQS Dead Letter Queue |
| **Pre-Signed URL** | A temporary secure URL that allows authorized users to download files directly from Blob Storage without passing through the application servers.  | AWS presigned URL |
| **Auth DB** | Stores Users, Credentials, Roles, Team Access Controls | PostgreSQL  |
| **DocBridge DB** | Stores Jobs, Tasks, Upload mappings, File metadata, Checksums, Processing status, Retry counts, Failure reasons | PostgreSQL  |
| **Platform DB** | Stores Team, Binders, Folder hierarchy, Final document metadata | PostgreSQL  |

**User Flows**

**Flow 1: User Authentication**

1. User logs in to the DocBridge app using username and password.  
2. Request is sent to Auth service.  
3. User record is retrieved from Auth Db and validates the password.  
4. A JWT token is generated with User ID and the team access controls.  
5. Token is stored for the session and the user should be able to see the dashboard.

   
**Flow 2: Browse Destination Hierarchy**

1. User logs in to the DocBridge application.  
2. User is authenticated and fetches team memberships using DocBridge API  
3. Teams, Binders and Folder hierarchy is returned.  
4. Unauthorized destinations are not accessible for the user and displays only accessible destinations

**Flow 3: Upload** 

1. User logs in to the DocBridge application.  
2. User selects one or more documents(up to 100\) to the bulk distribution for upload.  
3. User see all the documents in the Imported documents tab.  
4. User map each file to one or more destinations and submit.  
5. The system acknowledges the submission and a new job record is created and adds tasks to the Task Queue.  
6. Users see progress of the documents that are being uploaded on the job status page.  
7. Documents are stored in a database.

**Flow 4: Background Task Processing**

1. Once the job is submitted, the job worker consumes a task message from the queue.  
2. Task status is updated to In-progress.  
3. Checksum integrity is verified and the file is uploaded to Platform API.  
4. Job status is calculated and updated to Completed upon successful upload  
5. If failed, retry automatically three times. Update retry count and move to the dead letter queue if the job fails.  
   

**Flow 5: Track job Progress**

1. User opens job history  
2. Requests job details, job and task records are retrieved.  
3. Current status of the tasks are returned.  
4. Aggregate Job status is calculated and progress bar is displayed.  
    

**Flow 6: Retrieve/Download document**

1. User opens the completed job.  
2. Selects an uploaded file and clicks download.  
3. Download URL is requested and validates authorization.  
4. A pre-signed URL is generated and returned.  
5. User has the access to download the file directly from the blob storage.

 

**Decisions with Architectural Impact(In-Progress)**

Only decisions that stakeholders need to see. Implementation-level decisions go in the TDD.

[Architectural Decision Records]()

**Technical Design(In Progress)**

[Technical Design Document (TDD)](https://docs.google.com/document/d/1heifkLIYKxDmBgpiHjifVUWwVPvxJ9a0qCVU7cBuo_A/edit?tab=t.j1ccesser9tj)

Once the architecture and user flow above are aligned with stakeholders and the design team, engineering writes the TDD. All schemas, API contracts, task estimates, edge cases, and the launch plan live there.

**Releases**

**Release 1: Core Pipeline**

Target Date:TBD **\|** Status:Planned **\|** Total Estimate: \[Based on TDD\]

**Scope (feature level):**

* User Registration and Login  
* JWT Authentication  
* Team-based Authorization  
* Destination Discovery (Teams, Binders, Folders)  
* Multi-file Upload  
* Multi-destination Mapping  
* Job Creation  
* Blob Storage Integration  
* Asynchronous Task Processing  
* Task Queue  
* Job Worker  
* Retry Mechanism (3 Attempts)  
* Dead Letter Queue  
* Job Status Tracking  
* Secure File Download (Pre-Signed URL)  
* Docker Compose Local Deployment  
* Cloud Deployment (AWS)

**Dependencies:**

* AWS Account  
* AWS S3  
* Message Queue Service  
* PostgreSQL Database  
* Docker Desktop  
* Cloud IAM Configuration  
  


**Success Metrics:**

| Metric | Target |
| ----- | ----- |
| Job submission acknowledgement | \< 500 ms (P99) |
| Successful task delivery rate | \> 99.9% |
| File integrity validation | 100% |
| Retry success rate | \> 95% of transient failures recovered |
| Unauthorized access | 0 successful unauthorized requests |
| File download | Direct from Blob Storage |

**Risks:**

| Risk | Mitigation |
| ----- | ----- |
| Large file uploads increase API latency | Stream uploads directly to Blob Storage instead of buffering entirely in memory. |
| Queue backlog during traffic spikes | Scale Job Worker instances horizontally. |
| Document Platform API outages | Automatic retries with exponential backoff and Dead Letter Queue. |
| Blob Storage unavailability | Use cloud-managed high-availability storage with retry logic. |
| Database contention | Use indexing, transactions, and connection pooling. |

**Release 2: \[Enhancement Name\]**

Target Date: TBD \| Status: \-

**Scope (feature level):**

* Monitoring & Logging feature

**Dependencies:**

* Opentelemetry  
* Prometheus  
* Grafana/Splunk