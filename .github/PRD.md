# Product Requirements Document (PRD)
## Project: CoupleSync - The Ultimate Budget App for Couples

### 1. Overview
**Objective:** Build the best, highly usable budget management application specifically designed for couples. 
**Target Audience:** Couples wanting shared visibility and management of their finances.
**Scale Scope:** Initially limited to a small user base (maximum 10 users / 5 couples). High availability and extreme scalability are non-goals at this stage. 
**Platform:** Android Ecosystem (optimized for Samsung/Android devices).

### 2. Core Features
* **Automatic Account Tracking:** Capture Android bank push notifications, parse transactions on-device, and sync structured events to backend automatically.
* **Bank-Grade Security:** Encrypted data transit, secure user authentication, and permission-based notification capture with no stored banking credentials.
* **Personalized Dashboard:** A home screen customized to display priority goals, alerts, and preferred widgets.
* **Real-time Alerts & Due Date Reminders:** Push notifications for significant account events and upcoming bills.
* **Personalized Savings Goals:** Ability to set and track custom financial goals (e.g., "Vacation Fund").
* **Custom Spending & Net Worth Charts:** Visual representations of historical net worth and spending breakdowns over time.
* **Budget Category Customization & Re-categorization:** Ability to create custom categories and manually re-categorize imported transactions.
* **Split Transactions:** Capability to divide a single transaction into multiple categories or split the financial responsibility.
* **Household Member Add-ons:** Ability to invite additional family members (e.g., dependents) with distinct access levels.
* **Shared Access Across Devices:** Real-time cloud synchronization across Android devices.
* **Projected Cash Flows:** Algorithms to estimate future balances based on recurring expenses and income.

### 3. Technology Stack Options & Decisions
Based on the project constraints (rapid development, usability focus, limited initial users, Android target), the following stack has been selected:

* **Frontend (Mobile App):** React Native (using Expo).
    * *Purpose:* Handles the UI/UX. Provides a native Android feel with rapid development cycles.
* **Backend (API):** .NET 8 (C#) Web API.
    * *Purpose:* Handles business logic, data processing, projection calculations, and authentication.
* **Database:** PostgreSQL.
    * *Purpose:* Relational database to securely store users, transactions, goals, and configuration data.
* **Banking Integration:** Android Notification Listener (NotificationListenerService via mobile native bridge).
    * *Purpose:* Read supported bank push notifications on-device, parse transaction data locally, and send structured events to backend without third-party bank APIs.
    * *Limitation:* No retroactive transaction history; capture starts from app install and permission grant date.
* **Push Notifications:** Firebase Cloud Messaging (FCM).
    * *Purpose:* Delivering real-time alerts to Android devices.

### 4. Architectural Approach
**Monolithic Client-Server Architecture**
Since the app only needs to serve ~10 users initially, a microservices architecture would introduce unnecessary overhead. 
* **Client:** The React Native Android app communicates with the backend via RESTful JSON APIs.
* **Server:** A single .NET 8 Web API application hosting all endpoints (Users, Transactions, Goals, Integrations).
* **Background Jobs:** The .NET backend runs background services for alert evaluation and FCM dispatch only; transaction ingestion is event-driven by mobile uploads from the notification listener.

### 5. Data Model (High-Level)
* **Couple:** `Id`, `JoinCode`, `CreatedAt`
* **User:** `Id`, `CoupleId`, `Name`, `Email`, `DeviceToken` (for FCM)
* **Account:** `Id`, `UserId`, `InstitutionName`, `Balance`, `Type`
* **Transaction:** `Id`, `AccountId`, `Amount`, `Date`, `Category`, `Description`
* **Goal:** `Id`, `CoupleId`, `Title`, `TargetAmount`, `CurrentAmount`, `Deadline`

### 6. Deployment Strategy
* **Backend & Database:** Hosted on a simple PaaS (Platform as a Service) like Railway, Render, or a basic DigitalOcean Droplet. This keeps ops simple.
* **Android App:** Developed using Expo. For the 10 users, distribution will be done via direct APK sharing or Google Play Internal Testing track.

### 7. Future Considerations (Out of Scope for V1)
* iOS Support
* Microservices breakdown
* Advanced AI-driven categorization