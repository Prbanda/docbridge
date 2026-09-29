# AWS account access (first time)

Do this once so your machine can talk to AWS. Default region: **us-east-1**.

Step by step instructions: [AWS_SETUP](https://docs.google.com/document/d/1XRzRmyti_pxHyr_VoQzT8AvsQDsv3sLrtL47b_zujLY/edit?tab=t.rla1axbhdl20#heading=h.tfma9c8f8yqk)

## 1. Create an IAM user (console)

1. Open [IAM → Users](https://us-east-1.console.aws.amazon.com/iam/home?region=us-east-1#/users).
2. **Create user** → name it something like `YOURNAME-admin`.
3. Skip console access (CLI-only is enough).
4. Attach policy **AdministratorAccess** directly → Create.

## 2. Create an access key

1. Open that user → **Security credentials** → **Create access key**.
2. Choose **Command Line Interface (CLI)** → create.
3. Copy the **Access key ID** and **Secret access key** now — the secret is shown only once.

## 3. Install and configure the AWS CLI

1. Install the [AWS CLI](https://aws.amazon.com/cli/) if needed.
2. Configure:

```bash
aws configure
# AWS Access Key ID: <from step 2>
# AWS Secret Access Key: <from step 2>
# Default region name: us-east-1
# Default output format: json
```

3. Verify:

```bash
aws sts get-caller-identity
```

You should see your Account, UserId, and Arn. Then continue with [SETUP.md](SETUP.md).
